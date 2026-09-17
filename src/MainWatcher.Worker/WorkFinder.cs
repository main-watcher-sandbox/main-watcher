using System.Globalization;
using MainWatcher.Core;

namespace MainWatcher.Worker;

/// <summary>
/// Why a target needs a <c>watch.yml</c> cycle. <see cref="Check"/> and <see cref="ReportableSince"/> are set only when the work
/// is a report the Reporter already owes (ADR-013): <see cref="ReportableSince"/> is when the <c>main-watcher</c> job completed,
/// or default when GitHub gave no time for it.
/// </summary>
public sealed record Work(string Reason, long? Check = null, DateTimeOffset ReportableSince = default);

/// <summary>
/// Decides whether a target has work for <c>watch.yml</c> (ADR-010, ADR-013, ADR-017). It reads through <c>mw-observer</c> only
/// and uses the Planner's and Reporter's own rules, so the worker never starts a cycle they would not act on.
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
                if (jobs is null) return new($"check {check.Id}: target run {runId} was deleted", check.Id);
                if (Outcomes.Read(jobs) is not null)
                    return new($"check {check.Id}: the main-watcher job of target run {runId} has completed", check.Id,
                        jobs.Where(j => Outcomes.IsTestJob(j.Name)).Select(j => j.CompletedAt).FirstOrDefault() ?? default);
                continue;
            }
            // An unlinked check (a lost dispatch response): Planner.Recover links a single matching run, or releases the check
            // once the dispatch window has passed with none. An ambiguous match stays pending there, so it is not work here.
            var matches = Planner.RunsFor(check, await github.Runs(repo, GitHubGateway.Workflow, check.StartedAt.AddSeconds(-2), ct));
            if (matches.Length == 1) return new($"check {check.Id}: target run {matches[0].Id} awaits linking");
            if (matches.Length == 0 && now - check.StartedAt >= Planner.DispatchWindow)
                return new($"check {check.Id}: no target run appeared within {Planner.DispatchWindow.TotalMinutes:0} minutes");
        }
        var head = await github.MainHead(repo, ct);
        return Eligibility.CanStart(head, checks, TimeSpan.FromMinutes(target.PollInterval), now)
            ? new($"head {head[..Math.Min(7, head.Length)]} is eligible for a test") : null;
    }
}
