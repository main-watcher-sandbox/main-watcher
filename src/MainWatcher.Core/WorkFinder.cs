using System.Globalization;

namespace MainWatcher.Core;

/// <summary>
/// Why a target needs a <c>watch.yml</c> cycle, and since when.
/// <para>
/// <see cref="Since"/> is when the work became available: the <c>main-watcher</c> job's completion for a report the Reporter
/// owes (ADR-013), the check run's creation for one awaiting linking, the end of the dispatch window for one that never got a
/// target run, the deadline passed or the stop asked for by a run that must be cancelled (ADR-013 point 5), and the push that
/// made an eligible head current (ADR-017), and the moment an open lock's lease wanted renewing (ADR-014). It is null when
/// GitHub does not date the work — a
/// deleted target run, or a head whose push the activity read does not name — so the hourly sweep never judges how long work
/// has waited from a guess. A closed lock still owing reconciliation is dated by its closure (ADR-015), and an unfinished queue
/// sweep by the generation it owes (ADR-016). <see cref="Check"/> is set only when the work is a report already owed, and
/// <see cref="Sweep"/> only when it is a queue sweep: each is a debt the worker times, and says so when it is not paid.
/// </para>
/// </summary>
public sealed record Work(string Reason, DateTimeOffset? Since = null, long? Check = null, int? Sweep = null);

/// <summary>
/// What one look at a target found: <see cref="Work"/> is why it needs a cycle, and the rest are debts the worker times, which
/// it must see whether or not they are the reason for this cycle.
/// <para>
/// <see cref="Sweep"/> is the queue sweep an open lock still owes (ADR-016). It is reported separately because only one reason
/// can be the reason: a report owed or an eligible head is found first, and a target whose reports keep failing would
/// otherwise have its sweep debt hidden for as long as that lasts — exactly when merge groups queued before the lock are
/// still free to merge (PR #56 review).
/// </para>
/// </summary>
public sealed record Findings(Work? Work, Work? Sweep);

/// <summary>
/// Decides whether a target has work for <c>watch.yml</c> (ADR-010, ADR-013, ADR-017). It reads through <c>mw-observer</c> only
/// and uses the Planner's and Reporter's own rules, so the worker never starts a cycle they would not act on. The hourly sweep
/// asks the same question to see how long work has been waiting for the worker.
/// </summary>
/// <param name="queueDeadline">
/// The ADR-013 queue deadline of each target, which the Planner must be given to the same value; the sandbox shortens it for
/// chosen targets (TS-S16 (g)).
/// </param>
/// <param name="botLogin">The App whose lock issues carry a lease, as the Planner, the Reporter and the gate all read it.</param>
public sealed class WorkFinder(Func<DateTimeOffset>? clock = null, Func<string, TimeSpan>? queueDeadline = null,
    string botLogin = Reporter.DefaultBotLogin)
{
    /// <summary>Why the target needs a <c>watch.yml</c> cycle, and what it owes whatever that reason turns out to be.</summary>
    public async Task<Findings> Find(Target target, IGitHubGateway github, CancellationToken ct)
    {
        var repo = target.Repo;
        var checks = await github.Checks(repo, ct);
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var work = await Pending(target, github, checks, now, ct);
        if (work is null)
        {
            var head = await github.MainHead(repo, ct);
            var interval = TimeSpan.FromMinutes(target.PollInterval);
            if (Eligibility.CanStart(head, checks, interval, now))
                work = new($"head {head[..Math.Min(7, head.Length)]} is eligible for a test",
                    await EligibleSince(github, repo, head, checks, interval, ct));
        }
        // The open locks are read whatever has been found already, because the queue sweep is a debt this worker times and a
        // reason found earlier must not hide it (PR #56 review). It is the same read the lease renewal below needs, so a
        // target still costs one issue read a cycle (R-13).
        var locks = (await github.OpenIssues(repo, Reporter.LockLabel, ct))
            .Where(i => i.Author == botLogin && i.AuthorType == "Bot").OrderBy(i => i.Number).ToArray();
        // ADR-016: a sweep owed is a merge group that may still merge onto a red `main`. Only an open lock owes one: a closed
        // lock enforces nothing, and a debt nothing could discharge would ask for a cycle for ever.
        var sweep = locks.Select(issue => (Issue: issue, Required: QueueSweep.Owed(issue.Body)))
            .FirstOrDefault(l => l.Required is not null) is { Required: { } required, Issue: { } locked }
            ? new Work($"lock #{locked.Number}: its queue sweep for {Markers.Stamp(required)} is unfinished",
                required, Sweep: locked.Number)
            : null;
        // The sweep is asked for before the lease the same read was made for: a lease keeps a lock enforced, while the sweep is
        // what stops a group whose gate has already passed.
        work ??= sweep ?? Renewal(locks, target, now);
        work ??= await Unreconciled(repo, github, now, ct);
        return new(work, sweep);
    }

    /// <summary>The oldest in-progress check run with work the Planner or the Reporter would act on, or null when none has.</summary>
    async Task<Work?> Pending(Target target, IGitHubGateway github, IReadOnlyList<CheckRun> checks, DateTimeOffset now,
        CancellationToken ct)
    {
        var repo = target.Repo;
        foreach (var check in checks.Where(c => c.Status != "completed").OrderBy(c => c.StartedAt))
        {
            if (long.TryParse(check.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var runId))
            {
                // The Reporter's own test: a completed main-watcher job, whatever other jobs in the run are doing, or a deleted run.
                // Each of these is a report already owed, so the check run is pending, never stale (ADR-013 points 5 and 6).
                var jobs = await github.Jobs(repo, runId, ct);
                // A deleted run leaves nothing to date the work by: only the worker's own clock can, and only from now on.
                if (jobs is null) return new($"check {check.Id}: target run {runId} was deleted", Check: check.Id);
                var outcome = Outcomes.Read(jobs, now);
                // Not owed yet: GitHub is still writing the job's steps down. The worker reads the job again next cycle rather
                // than start a watch.yml run that would find nothing to report; once the steps are final or the wait is over,
                // the report is owed, dated from the job's completion as before (ADR-019).
                if (outcome is { Kind: OutcomeKind.StepsNotFinal }) continue;
                if (outcome is not null)
                    return new($"check {check.Id}: the main-watcher job of target run {runId} has completed",
                        jobs.Where(j => Outcomes.IsTestJob(j.Name)).Select(j => j.CompletedAt).FirstOrDefault(), check.Id);
                // A job that has not completed has two deadlines, and past either one the Planner must stop the run before it
                // can be judged (ADR-013 point 5). What the marker step shows says nothing here: the job is still going, so no
                // row of the outcome table applies, and it stays flagged until the run has stopped. This is not a report owed,
                // so it carries no check ID: the "reporting pending" alert times reports, not runs.
                if (StaleRun.TestJob(jobs) is { } job
                    && StaleRun.State(target, check, job, now, queueDeadline?.Invoke(repo)) is { Stage: not StaleStage.None } stale)
                    return new($"check {check.Id}: target run {runId} " + stale.Stage switch
                    {
                        StaleStage.Queue => $"did not start within {(queueDeadline?.Invoke(repo) ?? StaleRun.DefaultQueueDeadline).TotalMinutes:0} minutes",
                        StaleStage.Run => "has run past its deadline",
                        _ => "was asked to stop and has not stopped"
                    }, stale.Since);
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
        return null;
    }

    /// <summary>
    /// ADR-014: an open lock is enforced only while the watcher keeps renewing its lease, and renewal must not depend on the
    /// unreliable schedule (C-7).
    /// </summary>
    static Work? Renewal(IEnumerable<Issue> locks, Target target, DateTimeOffset now)
    {
        foreach (var issue in locks)
        {
            if (!Lease.RenewalDue(issue.Body, target.LockLease, now)) continue;
            var due = Lease.Due(issue.Body, target.LockLease, now);
            return new($"lock #{issue.Number}: its lease " + (due is null
                ? "is missing or unreadable" : $"has wanted renewing since {Markers.Stamp(due.Value)}"), due);
        }
        return null;
    }

    /// <summary>
    /// ADR-015 point 6: a lock that has closed still owes reports for the merges made during it, and nothing else would ask
    /// for a cycle once it is closed. Closures older than reconcile_lookback are not revisited (R-21), which is what bounds
    /// this read: the same Issues: read permission the lease uses, over a 30-day window. It is made only once nothing else has
    /// asked for a cycle, so an idle target pays for it and a busy one does not.
    /// </summary>
    async Task<Work?> Unreconciled(string repo, IGitHubGateway github, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var issue in (await github.Issues(repo, Reporter.LockLabel, now - Reporter.ReconcileLookback, ct))
            .Where(i => i.Author == botLogin && i.AuthorType == "Bot" && i.State == "closed" && !Reconciliation.IsComplete(i.Body))
            .OrderBy(i => i.Number))
            return new($"lock #{issue.Number}: it closed without being reconciled", issue.ClosedAt);
        return null;
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
