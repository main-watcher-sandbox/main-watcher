using System.Globalization;

namespace MainWatcher.Core;

/// <summary>
/// Why a target needs a <c>watch.yml</c> cycle, and since when.
/// <para>
/// <see cref="Since"/> is when the work became available: the <c>main-watcher</c> job's completion for a report the Reporter
/// owes (ADR-013), the check run's creation for one awaiting linking, the end of the dispatch window for one that never got a
/// target run, and the push that made an eligible head current (ADR-017). It is null when GitHub does not date the work — a
/// deleted target run, or a head whose push the activity read does not name — so the hourly sweep never judges how long work
/// has waited from a guess. <see cref="Check"/> is set only when the work is a report already owed.
/// </para>
/// </summary>
public sealed record Work(string Reason, DateTimeOffset? Since = null, long? Check = null);

/// <summary>
/// Decides whether a target has work for <c>watch.yml</c> (ADR-010, ADR-013, ADR-017). It reads through <c>mw-observer</c> only
/// and uses the Planner's and Reporter's own rules, so the worker never starts a cycle they would not act on. The hourly sweep
/// asks the same question to see how long work has been waiting for the worker.
/// </summary>
public sealed class WorkFinder(Func<DateTimeOffset>? clock = null)
{
    /// <summary>Why the target needs a <c>watch.yml</c> cycle, or null when it has no work.</summary>
    public async Task<Work?> Find(Target target, IGitHubGateway github, CancellationToken ct)
    {
        var repo = target.Repo;
        var checks = await github.Checks(repo, ct);
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        foreach (var check in checks.Where(c => c.Status != "completed").OrderBy(c => c.StartedAt))
        {
            if (long.TryParse(check.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var runId))
            {
                // The Reporter's own test: a completed main-watcher job, whatever other jobs in the run are doing, or a deleted run.
                // Each of these is a report already owed, so the check run is pending, never stale (ADR-013 points 5 and 6).
                var jobs = await github.Jobs(repo, runId, ct);
                // A deleted run leaves nothing to date the work by: only the worker's own clock can, and only from now on.
                if (jobs is null) return new($"check {check.Id}: target run {runId} was deleted", Check: check.Id);
                if (Outcomes.Read(jobs) is not null)
                    return new($"check {check.Id}: the main-watcher job of target run {runId} has completed",
                        jobs.Where(j => Outcomes.IsTestJob(j.Name)).Select(j => j.CompletedAt).FirstOrDefault(), check.Id);
                continue;
            }
            // An unlinked check (a lost dispatch response): Planner.Recover links a single matching run, or releases the check
            // once the dispatch window has passed with none. An ambiguous match stays pending there, so it is not work here.
            var matches = Planner.RunsFor(check, await github.Runs(repo, GitHubGateway.Workflow, check.StartedAt.AddSeconds(-2), ct));
            if (matches.Length == 1) return new($"check {check.Id}: target run {matches[0].Id} awaits linking", check.StartedAt);
            if (matches.Length == 0 && now - check.StartedAt >= Planner.DispatchWindow)
                return new($"check {check.Id}: no target run appeared within {Planner.DispatchWindow.TotalMinutes:0} minutes",
                    check.StartedAt + Planner.DispatchWindow);
        }
        var head = await github.MainHead(repo, ct);
        var interval = TimeSpan.FromMinutes(target.PollInterval);
        return Eligibility.CanStart(head, checks, interval, now)
            ? new($"head {head[..Math.Min(7, head.Length)]} is eligible for a test",
                await EligibleSince(github, repo, head, checks, interval, ct))
            : null;
    }

    /// <summary>
    /// When the head became eligible: the later of the push that made it the head of <c>main</c> and the moment ADR-017's own
    /// interval expired. Null when the activity read does not name that push, or could not be read at all, so work whose age is
    /// unknown is never reported as old. The read is made only for a head that is eligible now, so an idle target does not pay
    /// for it (R-13).
    /// </summary>
    static async Task<DateTimeOffset?> EligibleSince(IGitHubGateway github, string repo, string head,
        IReadOnlyList<CheckRun> checks, TimeSpan interval, CancellationToken ct)
    {
        IReadOnlyList<Push> pushes;
        try { pushes = await github.Pushes(repo, PushList.Limit, ct); }
        catch (Exception e) when (e is HttpRequestException or IOException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
        if (pushes.Where(p => p.After == head).Max(p => (DateTimeOffset?)p.Timestamp) is not { } pushed) return null;
        var waited = new List<DateTimeOffset> { pushed };
        // The rule itself holds work back: no test within poll_interval of the last one, and a neutral result waits as long again.
        if (checks.Count > 0) waited.Add(checks.Max(c => c.StartedAt) + interval);
        if (checks.Where(c => c.Sha == head).OrderByDescending(c => c.StartedAt).ThenByDescending(c => c.Id).FirstOrDefault()
            is { Conclusion: "neutral", CompletedAt: { } completed }) waited.Add(completed + interval);
        return waited.Max();
    }
}
