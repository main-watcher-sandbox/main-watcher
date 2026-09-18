namespace MainWatcher.Core;

/// <summary>Starts eligible heads and recovers unlinked dispatches (R-14, ADR-017).</summary>
public sealed class Planner(IGitHubGateway github, Func<DateTimeOffset>? clock = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null, Alerts? alerts = null, string botLogin = Reporter.DefaultBotLogin)
{
    /// <summary>Alerts that could not be raised, as "title: reason". They never stop a cycle, but the run reports them.</summary>
    public List<string> AlertFailures { get; } = [];

    /// <summary>
    /// Starts a test for the target's head when the shared rule allows it. The check runs are read here, inside
    /// <c>watch.yml</c>'s per-target concurrency group and immediately before the new check run is created, so a cycle that
    /// queued behind another cannot start a second test of the same head (ADR-017 point 3).
    /// </summary>
    public async Task<CheckRun?> Plan(Target target, bool force, CancellationToken ct)
    {
        if (!target.Enabled) return null;
        var sha = await github.MainHead(target.Repo, ct);
        var checks = await github.Checks(target.Repo, ct);
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        if (!Eligibility.CanStart(sha, checks, TimeSpan.FromMinutes(target.PollInterval), now, force))
        {
            // A forced dispatch is someone already dealing with this head; it needs no alert telling them to send one.
            if (!force && Eligibility.Capped(sha, checks)) await Untestable(target, sha, ct);
            return null;
        }
        await github.ValidateTarget(target, ct);
        var check = await github.CreateCheck(target.Repo, sha, now, ct);
        // Never retry a dispatch POST: a lost response may still have started the workflow.
        long? runId;
        try { runId = await github.Dispatch(target.Repo, sha, check.Id, ct); }
        catch (HttpRequestException e) when (e.StatusCode is { } status && (int)status is >= 400 and < 500)
        {
            await github.Complete(target.Repo, check.Id, "neutral", Outcomes.Title(OutcomeKind.Unknown), $"Dispatch rejected (HTTP {(int)status}); no target run started. Retry after poll_interval.", ct);
            throw;
        }
        for (var attempt = 0; runId is null && attempt < 6; attempt++)
        {
            runId = await FindRun(target.Repo, check, ct);
            if (runId is null) await (delay ?? Task.Delay)(TimeSpan.FromSeconds(5), ct);
        }
        return runId is null ? check : await Link(target.Repo, check, runId.Value, ct);
    }

    /// <summary>Waits for dispatch visibility, then releases a missing run after the 30-minute queue window.</summary>
    public async Task<CheckRun> Recover(string repo, CheckRun check, CancellationToken ct)
    {
        if (check.Status == "completed" || !string.IsNullOrEmpty(check.ExternalId)) return check;
        var matches = await MatchingRuns(repo, check, ct);
        if (matches.Length == 1) return await Link(repo, check, matches[0].Id, ct);
        // A successful empty lookup is required: API errors and ambiguous matches never
        // release the check, since there may still be an active target run.
        if (matches.Length == 0 && (clock ?? (() => DateTimeOffset.UtcNow))() - check.StartedAt >= DispatchWindow)
        {
            await github.Complete(repo, check.Id, "neutral", Outcomes.Title(OutcomeKind.Unknown), "Dispatch produced no discoverable target run within 30 minutes; retry after poll_interval.", ct);
            return check with { Status = "completed", Conclusion = "neutral" };
        }
        return check;
    }

    /// <summary>
    /// The ADR-017 "head untestable" alert: this head has spent its <see cref="Eligibility.Cap"/> attempts, so nothing but a push
    /// or a forced dispatch will test it again. It names any open lock, because a lock that outlives its head's last attempt can
    /// no longer close on its own (R-23). The hidden marker keys it to the head, so later cycles do not repeat it; a new head
    /// that also runs out gets its own comment on the same alert.
    /// </summary>
    async Task Untestable(Target target, string sha, CancellationToken ct)
    {
        var repo = target.Repo;
        var title = $"Head untestable on {repo}";
        if (alerts is null) { AlertFailures.Add($"{title}: no alert sink configured"); return; }
        try
        {
            var open = (await github.OpenIssues(repo, Reporter.LockLabel, ct))
                .Where(i => i.Author == botLogin && i.AuthorType == "Bot").OrderBy(i => i.Number).ToArray();
            await alerts.Raise(title,
                $"`main` of `{repo}` is at {Markdown.Commit(repo, sha)}, which has {Eligibility.Cap} `neutral` check runs and no "
                + "result since, so the tests have never run to completion on it. Main Watcher has stopped testing this head "
                + "(ADR-017). The earlier `watcher-infra` alerts on this target say why each attempt gave no result.\n\n"
                + (open.Length == 0
                    ? "No lock is open, so a failure on this head would go unreported."
                    : "These locks cannot close on their own while this head is untestable:\n\n"
                        + string.Join("\n", open.Select(i => $"- #{i.Number} ({i.Url})")))
                + $"\n\nTesting resumes when a push creates a new head, or when `watch.yml` is dispatched for `{repo}` with "
                + "`force: true`, which ignores only this cap.", ct, $"<!-- main-watcher untestable sha={sha} -->");
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { AlertFailures.Add($"{title}: {e.Message}"); }
    }

    async Task<CheckRun> Link(string repo, CheckRun check, long runId, CancellationToken ct)
    {
        await github.Link(repo, check.Id, runId, ct);
        return check with { ExternalId = runId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }

    public async Task<long?> FindRun(string repo, CheckRun check, CancellationToken ct)
    {
        // The caller exposes its sha input in run-name. head_sha describes the dispatch
        // ref, which can move independently of the commit under test (R-14).
        var matches = await MatchingRuns(repo, check, ct);
        return matches.Length == 1 ? matches[0].Id : null;
    }

    async Task<WorkflowRun[]> MatchingRuns(string repo, CheckRun check, CancellationToken ct) =>
        RunsFor(check, await github.Runs(repo, GitHubGateway.Workflow, check.StartedAt.AddSeconds(-2), ct));

    /// <summary>Target runs that could belong to an unlinked check; the trigger worker reads them the same way.</summary>
    public static WorkflowRun[] RunsFor(CheckRun check, IEnumerable<WorkflowRun> runs) =>
        runs.Where(r => r.Title == $"main-watcher-tests {check.Sha}" && r.CreatedAt >= check.StartedAt.AddSeconds(-2)).ToArray();

    /// <summary>How long an unlinked check waits for its target run before <see cref="Recover"/> completes it as neutral.</summary>
    public static readonly TimeSpan DispatchWindow = TimeSpan.FromMinutes(30);
}
