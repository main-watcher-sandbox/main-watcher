namespace MainWatcher.Core;

/// <summary>Starts eligible heads and recovers unlinked dispatches (R-14, ADR-017).</summary>
public sealed class Planner(IGitHubGateway github, Func<DateTimeOffset>? clock = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public async Task<CheckRun?> Plan(Target target, bool force, CancellationToken ct)
    {
        if (!target.Enabled) return null;
        var sha = await github.MainHead(target.Repo, ct);
        var checks = await github.Checks(target.Repo, ct);
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        if (!Eligibility.CanStart(sha, checks, TimeSpan.FromMinutes(target.PollInterval), now, force)) return null;
        await github.ValidateTarget(target, ct);
        var check = await github.CreateCheck(target.Repo, sha, now, ct);
        // Never retry a dispatch POST: a lost response may still have started the workflow.
        long? runId;
        try { runId = await github.Dispatch(target.Repo, sha, check.Id, ct); }
        catch (HttpRequestException e) when (e.StatusCode is { } status && (int)status is >= 400 and < 500)
        {
            await github.Complete(target.Repo, check.Id, "neutral", $"Dispatch rejected (HTTP {(int)status}); no target run started. Retry after poll_interval.", ct);
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
        if (matches.Length == 0 && (clock ?? (() => DateTimeOffset.UtcNow))() - check.StartedAt >= TimeSpan.FromMinutes(30))
        {
            await github.Complete(repo, check.Id, "neutral", "Dispatch produced no discoverable target run within 30 minutes; retry after poll_interval.", ct);
            return check with { Status = "completed", Conclusion = "neutral" };
        }
        return check;
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
        (await github.Runs(repo, check.StartedAt.AddSeconds(-2), ct))
            .Where(r => r.Title == $"main-watcher-tests {check.Sha}"
                && r.CreatedAt >= check.StartedAt.AddSeconds(-2)).ToArray();
}
