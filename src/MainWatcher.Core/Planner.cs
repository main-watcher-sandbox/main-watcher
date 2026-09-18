namespace MainWatcher.Core;

/// <summary>
/// Starts eligible heads, recovers unlinked dispatches (R-14, ADR-017), renews open locks' leases (ADR-014) and stops target
/// runs that have passed a deadline (ADR-013 point 5).
/// </summary>
/// <param name="queueDeadline">The ADR-013 queue deadline; the sandbox shortens it (TS-S16 (g)).</param>
/// <param name="cancelsRuns">
/// Whether a stop request is really sent. The sandbox sets it false so that every cancel fails, which is how TS-S16 (h)
/// reaches "target run could not be stopped" without a run GitHub genuinely cannot stop.
/// </param>
/// <param name="afterWrite">
/// Called after each lock-issue write, by name, so the sandbox fault switch can stop a cycle between two of them
/// (TS-S17 (b)); the Reporter's own writes go through the same switch.
/// </param>
public sealed class Planner(IGitHubGateway github, Func<DateTimeOffset>? clock = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null, Alerts? alerts = null, string botLogin = Reporter.DefaultBotLogin,
    TimeSpan? queueDeadline = null, bool cancelsRuns = true, Action<string>? afterWrite = null)
{
    /// <summary>The check run's output title while its target run is testing.</summary>
    public const string TestingTitle = "Tests running";

    /// <summary>The check run's output title while its target run is being stopped.</summary>
    public const string StaleTitle = "Stopping a stale target run";

    /// <summary>Alerts that could not be raised, as "title: reason". They never stop a cycle, but the run reports them.</summary>
    public List<string> AlertFailures { get; } = [];

    DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();

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
        var now = Now;
        if (!Eligibility.CanStart(sha, checks, TimeSpan.FromMinutes(target.PollInterval), now, force))
        {
            // A forced dispatch is someone already dealing with this head; it needs no alert telling them to send one.
            if (!force && Eligibility.Capped(sha, checks)) await Untestable(target, sha, ct);
            return null;
        }
        await github.ValidateTarget(target, ct);
        // The target's timeout is recorded with the check run, because it is what the target run about to be dispatched will
        // carry for its whole life. The deadlines are judged against this value, not against targets.yml as it reads later,
        // so editing a target's timeout never moves the deadline of a job already running (ADR-013, StaleRun.TestTimeout).
        var check = await github.CreateCheck(target.Repo, sha, now, TestingTitle,
            Markers.Set($"Main Watcher is testing {Markdown.Commit(target.Repo, sha)}.",
                (StaleRun.TimeoutMinutes, target.Timeout.ToString(System.Globalization.CultureInfo.InvariantCulture))), ct);
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
        if (matches.Length == 0 && Now - check.StartedAt >= DispatchWindow)
        {
            await github.Complete(repo, check.Id, "neutral", Outcomes.Title(OutcomeKind.Unknown), "Dispatch produced no discoverable target run within 30 minutes; retry after poll_interval.", ct);
            return check with { Status = "completed", Conclusion = "neutral" };
        }
        return check;
    }

    /// <summary>
    /// ADR-014: sets every open App lock's lease to now + the target's <c>lock_lease</c>, so that the gate keeps enforcing it.
    /// Renewal is what says the watcher still maintains this lock, so it happens on every cycle that processes the target,
    /// whether or not anything else in that cycle works: a watcher that cannot start a test is still a watcher, while one that
    /// has stopped renews nothing and the gate lets ordinary merges through within <c>lock_lease</c> (NFR-3).
    /// <para>
    /// A lease that had already run out is a lapse. Its window and the ADR-016 sweep obligation are written in the <b>same</b>
    /// issue update as the new lease, so a crash right after the renewal cannot hide it; the comment, the alert and the
    /// <c>lapse_reported</c> marker follow, and a cycle that stops between them posts nothing twice.
    /// </para>
    /// </summary>
    /// <returns>A line per lock renewed, for the log.</returns>
    public async Task<IReadOnlyList<string>> Renew(Target target, CancellationToken ct)
    {
        var repo = target.Repo;
        var lines = new List<string>();
        foreach (var issue in (await github.OpenIssues(repo, Reporter.LockLabel, ct)).Where(IsApp).OrderBy(i => i.Number))
        {
            var now = Now;
            var until = now + target.LockLease;
            // A lock with no readable lease is not a lapse: the gate has been failing open, but nothing says since when, and a
            // guessed window would be reported as fact. The renewal alone puts the lock back under enforcement.
            var expired = Markers.Time(issue.Body, Lease.Until) is { } previous && previous <= now ? previous : (DateTimeOffset?)null;
            // A window whose comment and alert are still owed is kept, never replaced: a renewal that died before reporting,
            // followed by a second lapse, must leave both windows on the issue for this cycle or a later one to post.
            (string Name, string Value)[] fields = expired is null
                ? [(Lease.Until, Markers.Stamp(until))]
                : [(Lease.Until, Markers.Stamp(until)), (Lease.SweepRequired, Markers.Stamp(now)),
                    (Lease.Lapsed, Lease.Field([.. Lease.Unreported(issue.Body), new Lease.Lapse(expired.Value, now)]))];
            var body = Markers.Set(issue.Body, fields);
            await github.EditBody(repo, issue.Number, body, ct);
            afterWrite?.Invoke("renew");
            lines.Add($"Lock #{issue.Number}: lease renewed until {Markers.Stamp(until)}"
                + (expired is null ? "." : $"; it had lapsed at {Markers.Stamp(expired.Value)}."));
            if (Lease.Unreported(body) is { Count: > 0 } owed) await ReportLapse(target, issue, body, owed, ct);
        }
        // A lock that closed with a lapse still owed — a green run reports and closes before this runs, and a person can close
        // one at any time — keeps that debt. It needs no lease, so it is only reported (ADR-014 point 4).
        foreach (var issue in (await github.Issues(repo, Reporter.LockLabel, Now - Reporter.ReconcileLookback, ct))
            .Where(i => IsApp(i) && i.State == "closed").OrderBy(i => i.Number))
            if (Lease.Unreported(issue.Body) is { Count: > 0 } owed)
            {
                await ReportLapse(target, issue, issue.Body ?? "", owed, ct);
                lines.Add($"Lock #{issue.Number}: reported {owed.Count} lapse(s) it closed still owing.");
            }
        return lines;
    }

    /// <summary>
    /// Reports the lapses a renewal recorded, oldest first: a comment on the lock and a <c>watcher-infra</c> alert for each,
    /// then one <c>lapse_reported</c> marker saying how far the reporting got. Each carries its own window as a hidden key, so
    /// a replay after a crash between them creates nothing new (ADR-012, ADR-014 point 4). Like the neutral result's alert,
    /// these are required writes: a failure is thrown and the marker stays behind, so a later cycle posts what is missing.
    /// </summary>
    async Task ReportLapse(Target target, Issue issue, string body, IReadOnlyList<Lease.Lapse> lapses, CancellationToken ct)
    {
        if (alerts is null) throw new InvalidOperationException("A lock lapse cannot be reported without an alert sink.");
        var repo = target.Repo;
        // Read once: several owed windows are one replay, and each is told apart by its own key.
        var comments = await github.Comments(repo, issue.Number, null, ct);
        foreach (var lapse in lapses.OrderBy(l => l.At))
        {
            var key = $"<!-- main-watcher {Lease.Lapsed}={Markers.Stamp(lapse.From)}..{Markers.Stamp(lapse.At)} -->";
            // Three separate claims, because each can be false of the others' shape: what happened, what it allowed, and what
            // is true now. A coalesced span stands for several lapses with enforced stretches between them, and a lock that
            // has closed since is not enforced at all.
            var what = lapse.Count > 1
                ? $"This lock's lease ran out and was renewed {lapse.Count} times between {Markers.Stamp(lapse.From)} and "
                    + $"{Markers.Stamp(lapse.At)}. Main Watcher could not report each lapse as it happened, so they are "
                    + "recorded together and this window covers them all."
                : $"This lock's lease ran out at {Markers.Stamp(lapse.From)} and was renewed at {Markers.Stamp(lapse.At)}, "
                    + $"{(lapse.At - lapse.From).TotalMinutes:0} minutes later.";
            what += " While a lease is expired the gate fails open, so the merge queue accepted pull requests without the "
                + "`fixes-main` label during that window (ADR-014). "
                + (issue.State == "open"
                    ? "The lock is enforced again now."
                    : "This lock has closed since, so nothing is enforcing it now; the window is recorded for the merges made "
                        + "in it.");
            if (!comments.Any(c => c.Body.Contains(key, StringComparison.Ordinal)))
            {
                await github.Comment(repo, issue.Number, what + "\n\n" + key, ct);
                afterWrite?.Invoke("lapse");
            }
            await alerts.Raise($"Lock lease lapsed on {repo}",
                $"Lock {issue.Url} on `{repo}` was open but unenforced: {char.ToLowerInvariant(what[0])}{what[1..]}\n\n"
                + "Main Watcher renews a lease every cycle and the trigger worker asks for one each hour, so a lapse means the "
                + "watcher did not run for the whole of `lock_lease`: check `watch.yml`, the trigger worker and the `main-watcher` "
                + "App's credentials. Merges made during the window are reported by reconciliation, and merge groups queued then "
                + "have their gate re-run (ADR-015, ADR-016).", ct, key);
        }
        await github.EditBody(repo, issue.Number,
            Markers.Set(body, (Lease.Reported, Markers.Stamp(lapses.Max(l => l.At)))), ct);
        afterWrite?.Invoke("lapse_reported");
    }

    /// <summary>
    /// ADR-008 and ADR-015: reports every pull request that merged into <c>main</c> during a lock's window without
    /// <c>fixes-main</c>. The window is the issue's creation to its closure, or to now while it is open, and reconciliation
    /// follows a lock past its closure: a human can close one before the watcher has looked at a merge made during it, and
    /// nothing would ever look again. So every App lock that is open, or closed within <see cref="Reporter.ReconcileLookback"/>
    /// and not yet <c>reconciled=complete</c>, is processed, and the cursor lives in the issue either way.
    /// <para>
    /// Each merge's label history is read up to its <c>merged_at</c>, because the label a pull request carries now is not the
    /// one it merged with: a label added afterwards would hide a real report, and one removed afterwards would raise a false
    /// one. A history that cannot be read stops the pass at that merge, with the cursor left behind it, so the merge is judged
    /// on a later run rather than waved through.
    /// </para>
    /// </summary>
    /// <returns>A line per lock examined, for the log.</returns>
    public async Task<IReadOnlyList<string>> Reconcile(Target target, CancellationToken ct)
    {
        var repo = target.Repo;
        var now = Now;
        // An open lock counts even when nothing has updated it within the lookback. The later read wins on state.
        var locks = (await github.OpenIssues(repo, Reporter.LockLabel, ct))
            .Concat(await github.Issues(repo, Reporter.LockLabel, now - Reporter.ReconcileLookback, ct))
            .Where(IsApp).GroupBy(i => i.Number).Select(g => g.Last())
            .Where(i => !Reconciliation.IsComplete(i.Body)).OrderBy(i => i.Number).ToArray();
        if (locks.Length == 0) return [];
        // One activity read serves every lock: the windows all end at or before now, so they overlap in this one list.
        var activity = await github.Pushes(repo, PushList.Limit, ct);
        var lines = new List<string>();
        foreach (var issue in locks) lines.Add(await ReconcileLock(target, issue, activity, now, ct));
        return lines;
    }

    async Task<string> ReconcileLock(Target target, Issue issue, IReadOnlyList<Push> activity, DateTimeOffset now, CancellationToken ct)
    {
        var repo = target.Repo;
        var cursor = Markers.Time(issue.Body, Reconciliation.Cursor);
        // Without a cursor the window starts where the lock does, and a merge stamped in that same second is inside it; with
        // one, it starts after what has already been read. An issue GitHub does not date bounds nothing, so the activity read
        // is the whole window and no claim is made about anything older.
        var from = cursor ?? issue.CreatedAt;
        // ADR-015: a closed lock's window ends at its closure, and a merge in that same second counts as inside it.
        var until = issue.State == "closed" ? issue.ClosedAt ?? issue.UpdatedAt ?? now : now;
        var merges = activity.Where(p => Reconciliation.MergeActivity.Contains(p.Type) && p.Timestamp <= until
            && (from is not { } start || (cursor is null ? p.Timestamp >= start : p.Timestamp > start)))
            .OrderBy(p => p.Timestamp).ToArray();
        var rows = new List<string>();
        IReadOnlyList<IssueComment>? comments = null;
        var reached = cursor;
        string? stopped = null;
        DateTimeOffset? blocked = null;
        foreach (var merge in merges)
        {
            IReadOnlyList<MergedCommit>? commits;
            try { commits = await github.MergedCommits(repo, merge.Before, merge.After, ct); }
            catch (Exception e) when (Unreadable(e, ct))
            {
                (stopped, blocked) = ($"the commits of `{Markdown.Short(merge.After)}` could not be read ({e.Message})", merge.Timestamp);
                break;
            }
            var pulls = commits is null ? [] : Reconciliation.Pulls(commits);
            // A merge whose commits name no pull request, or whose range GitHub can no longer compare, is still a merge made
            // while `main` was locked. Nothing shows it carried the label, so it is reported as what is known about it.
            if (pulls.Count == 0)
            {
                comments = await ReportMerge(target, issue, Reconciliation.Key(merge.After),
                    $"A {(merge.Type == "pr_merge" ? "pull request merge" : "merge-queue merge")} left "
                    + $"{Markdown.Commit(repo, merge.After)} on `main` at {Markers.Stamp(merge.Timestamp)}. "
                    + (commits is null
                        ? "Its range can no longer be compared, so the pull request it merged cannot be named"
                        : "None of its commit subjects names a pull request, so its `fixes-main` label cannot be judged"),
                    rows, comments, ct);
                reached = merge.Timestamp;
                continue;
            }
            foreach (var (pull, committed) in pulls)
            {
                IReadOnlyList<PullEvent> events;
                try { events = await github.PullEvents(repo, pull, ct); }
                catch (Exception e) when (Unreadable(e, ct))
                {
                    (stopped, blocked) = ($"the label history of #{pull} could not be read ({e.Message})", merge.Timestamp);
                    break;
                }
                // The timeline's own merge is the moment ADR-015 judges the labels at. The commit's date only stands in when
                // the timeline holds no merge: a merge-queue commit is built before its group merges, so the two differ by
                // however long the queue took, and judging at the earlier one would report a label added while it waited.
                var mergedAt = Reconciliation.MergedAt(events) ?? committed;
                if (Reconciliation.WasFix(events, mergedAt)) continue;
                comments = await ReportMerge(target, issue, Reconciliation.Key(pull),
                    $"[#{pull}](https://github.com/{repo}/pull/{pull}) merged into `main` at {Markers.Stamp(mergedAt)} "
                    + $"without the `{Reconciliation.FixLabel}` label", rows, comments, ct);
            }
            if (stopped is not null) break;
            reached = merge.Timestamp;
        }
        // The activity read is bounded (ADR-003), so a window whose start it does not reach may hold merges nothing can see.
        if (activity.Count >= PushList.Limit && from is { } begin && activity.Min(p => p.Timestamp) > begin
            && !(issue.Body ?? "").Contains(Reconciliation.Truncated, StringComparison.Ordinal))
            rows.Add($"- The repository activity read reaches back only to {Markers.Stamp(activity.Min(p => p.Timestamp))}, "
                + "which is after this lock's window began, so merges older than that could not be checked. "
                + Reconciliation.Truncated);
        // Complete only once the whole window has been checked: a pass that stopped part-way is retried on the next run.
        var complete = stopped is null && issue.State == "closed";
        (string Name, string Value)[] fields = [
            .. reached is { } mark && mark != cursor ? new[] { (Reconciliation.Cursor, Markers.Stamp(mark)) } : [],
            .. complete ? new[] { (Reconciliation.Complete, Reconciliation.CompleteValue) } : []];
        if (rows.Count > 0 || fields.Length > 0)
        {
            var body = Reconciliation.Append(issue.Body, rows);
            await github.EditBody(repo, issue.Number, fields.Length > 0 ? Markers.Set(body, fields) : body, ct);
            afterWrite?.Invoke("reconcile");
        }
        await ReconcileFailing(target, issue, now, stopped, blocked, complete, ct);
        return $"Lock #{issue.Number}: reconciled {merges.Length} merge(s) up to {Markers.Stamp(until)}"
            + (rows.Count > 0 ? $", reporting {rows.Count}" : "")
            + (complete ? "; its window is complete." : stopped is null ? "." : $"; stopped because {stopped}.");
    }

    /// <summary>
    /// Reports one unlabelled merge: a row under "Merged while locked" in the issue body, a comment when the issue has closed
    /// so that its participants are notified, and a <c>watcher-infra</c> alert, which is the channel that does not depend on
    /// anyone watching a closed issue. Each carries the same hidden key, so a pass that stopped between them repeats none of
    /// them; the body row is collected and written once at the end, with the cursor.
    /// </summary>
    /// <returns>The issue's comments, read at most once per lock.</returns>
    async Task<IReadOnlyList<IssueComment>?> ReportMerge(Target target, Issue issue, string key, string what, List<string> rows,
        IReadOnlyList<IssueComment>? comments, CancellationToken ct)
    {
        if (alerts is null) throw new InvalidOperationException("A merge made during a lock cannot be reported without an alert sink.");
        var repo = target.Repo;
        if (issue.State == "closed")
        {
            comments ??= await github.Comments(repo, issue.Number, null, ct);
            if (!comments.Any(c => c.Body.Contains(key, StringComparison.Ordinal)))
            {
                await github.Comment(repo, issue.Number, $"{what}, while this lock was open. "
                    + "The lock has closed since, so this is a record rather than a block: the gate let the merge through, or "
                    + "the lock closed before the merge had been checked (ADR-015).\n\n" + key, ct);
                afterWrite?.Invoke("merged");
            }
        }
        await alerts.Raise($"Merged while locked on {repo}",
            $"{what}, while lock {issue.Url} on `{repo}` was open.\n\n"
            + "The gate should have kept it out of the merge queue. It did not, because it failed open on a GitHub API error, "
            + "because the lock's lease had expired, or because the merge group had passed its gate before the lock existed "
            + "(ADR-008, ADR-014, ADR-016). `main` may now be more broken than the lock says.", ct, key);
        if (!(issue.Body ?? "").Contains(key, StringComparison.Ordinal)) rows.Add($"- {what}. {key}");
        return comments;
    }

    /// <summary>
    /// ADR-015 point 7: a merge left unresolved, or a closed lock left unreconciled, for <see cref="Reconciliation.FailingAfter"/>
    /// is raised as "reconciliation failing". The key carries the day, so a fault that lasts is one comment a day rather than
    /// one per cycle, and a watcher that restarts cannot repeat it.
    /// </summary>
    async Task ReconcileFailing(Target target, Issue issue, DateTimeOffset now, string? stopped, DateTimeOffset? blocked,
        bool complete, CancellationToken ct)
    {
        var repo = target.Repo;
        var overdue = blocked is { } at && now - at >= Reconciliation.FailingAfter;
        var stale = !complete && issue.State == "closed" && issue.ClosedAt is { } closed && now - closed >= Reconciliation.FailingAfter;
        if (!overdue && !stale) return;
        var title = $"Reconciliation failing on {repo}";
        if (alerts is null) { AlertFailures.Add($"{title}: no alert sink configured"); return; }
        try
        {
            await alerts.Raise(title,
                $"Main Watcher has not finished reconciling lock {issue.Url} on `{repo}`.\n\n"
                // The closure is named first where it is the older fact: a lock left behind for a day says more than the merge
                // that blocked it, and every merge inside such a window is older than the closure anyway.
                + (stale
                    ? $"The lock closed at {Markers.Stamp(issue.ClosedAt!.Value)} and its window has still not been checked"
                        + (stopped is null ? ".\n\n" : $", because {stopped}.\n\n")
                    : $"A merge made at {Markers.Stamp(blocked!.Value)} is still unjudged: {stopped}.\n\n")
                + "Until it finishes, a pull request that merged during this lock without the `fixes-main` label may be "
                + $"unreported (NFR-4). Reconciliation is retried on every cycle for `{repo}`, and stops looking at this lock "
                + $"{Reporter.ReconcileLookback.TotalDays:0} days after it closed (ADR-015).", ct,
                $"<!-- main-watcher reconcile-failing issue={issue.Number} day={now.UtcDateTime:yyyy-MM-dd} -->");
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { AlertFailures.Add($"{title}: {e.Message}"); }
    }

    /// <summary>A read that failed for a reason a later run may not see: the pass stops here rather than judging without it.</summary>
    static bool Unreadable(Exception e, CancellationToken ct) =>
        e is HttpRequestException or IOException or System.Text.Json.JsonException
        || e is TaskCanceledException && !ct.IsCancellationRequested;

    /// <summary>
    /// ADR-013 point 5: a target run that has passed a deadline is stopped before it is judged. One step is taken per cycle,
    /// and each is recorded in the check run's output before the request it describes, so a crash resumes from the recorded
    /// times instead of starting the wait again:
    /// <list type="number">
    ///   <item>past the queue or run deadline, record <c>cancel_requested</c> and cancel;</item>
    ///   <item>while the run has not stopped, ask again, and after <see cref="StaleRun.StopWait"/> record
    ///     <c>force_cancel_requested</c> and force-cancel;</item>
    ///   <item>another <see cref="StaleRun.StopWait"/> on, raise "target run could not be stopped" and keep force-cancelling.</item>
    /// </list>
    /// The check run stays <c>in_progress</c> throughout, so <see cref="Eligibility.CanStart"/> starts no second test for the
    /// target — not a retry, not a newer head and not a forced dispatch — until the run stops or someone deletes it (R-24).
    /// Once it has stopped, its job goes through the outcome table like any other (ADR-013 point 1).
    /// </summary>
    /// <returns>What this cycle did, for the log, or null when there is nothing to stop.</returns>
    public async Task<string?> Stop(Target target, CheckRun check, CancellationToken ct)
    {
        var repo = target.Repo;
        if (check.Status == "completed" || !long.TryParse(check.ExternalId, out var runId)) return null;
        // A read that fails throws before anything is written, so nothing changes and the next cycle asks again.
        var jobs = await github.Jobs(repo, runId, ct);
        // A deleted run has nothing to stop, and a completed or duplicated job is the Reporter's to judge, not this rule's.
        if (StaleRun.TestJob(jobs) is not { } job) return null;
        var now = Now;
        var stage = StaleRun.State(target, check, job, now, queueDeadline).Stage;
        if (stage == StaleStage.None) return null;
        var run = $"[target run {runId}](https://github.com/{repo}/actions/runs/{runId})";
        // Every output this method writes replaces the last one, so the dispatched timeout is carried through each of them:
        // it is the only record of what this run's deadline was counted from.
        var kept = Markers.Field(check.Summary, StaleRun.TimeoutMinutes) is { } minutes
            ? new[] { (StaleRun.TimeoutMinutes, minutes) } : [];
        var asked = Markers.Time(check.Summary, StaleRun.CancelRequested);
        if (asked is null)
        {
            var why = stage == StaleStage.Queue
                ? $"did not get a runner within {(queueDeadline ?? StaleRun.DefaultQueueDeadline).TotalMinutes:0} minutes of this check run"
                : $"has run {StaleRun.RunGrace.TotalMinutes:0} minutes past the deadline its `timeout-minutes` allows";
            await Record(repo, check, $"The {run} {why}, so Main Watcher cancelled it. This check run stays in progress, and no "
                + $"test starts for `{repo}`, until the run has stopped (ADR-013).", ct,
                [.. kept, (StaleRun.CancelRequested, Markers.Stamp(now))]);
            return $"Check {check.Id}: target run {runId} passed its {(stage == StaleStage.Queue ? "queue" : "run")} deadline; "
                + await AskFor(repo, runId, false, ct);
        }
        var forced = Markers.Time(check.Summary, StaleRun.ForceCancelRequested);
        if (forced is null && now - asked.Value < StaleRun.StopWait)
            return $"Check {check.Id}: target run {runId} was cancelled at {Markers.Stamp(asked.Value)} and has not stopped; "
                + await AskFor(repo, runId, false, ct);
        if (forced is null)
        {
            await Record(repo, check, $"The {run} has not stopped in the {StaleRun.StopWait.TotalMinutes:0} minutes since it was "
                + "cancelled, so Main Watcher force-cancelled it. This check run stays in progress until the run stops (ADR-013).",
                ct, [.. kept, (StaleRun.CancelRequested, Markers.Stamp(asked.Value)), (StaleRun.ForceCancelRequested, Markers.Stamp(now))]);
            return $"Check {check.Id}: target run {runId} outlived its cancel; " + await AskFor(repo, runId, true, ct);
        }
        // The alert comes before the request it escalates: a force-cancel that keeps failing is exactly what it exists for.
        if (now - forced.Value >= StaleRun.StopWait) await Unstoppable(target, check, runId, asked.Value, forced.Value, ct);
        return $"Check {check.Id}: target run {runId} was force-cancelled at {Markers.Stamp(forced.Value)} and has not stopped; "
            + await AskFor(repo, runId, true, ct);
    }

    /// <summary>Writes the stale-run state into the check run's output, which leaves it <c>in_progress</c>.</summary>
    Task Record(string repo, CheckRun check, string text, CancellationToken ct, params (string Name, string Value)[] fields) =>
        github.Output(repo, check.Id, StaleTitle, Markers.Set(text, fields), ct);

    /// <summary>Asks GitHub to stop the run, unless the sandbox switch is making that fail, and says what came of it.</summary>
    async Task<string> AskFor(string repo, long runId, bool force, CancellationToken ct)
    {
        var what = force ? "force-cancel" : "cancel";
        var refusal = cancelsRuns ? await github.CancelRun(repo, runId, force, ct) : "the sandbox cancel switch refused it";
        return refusal is null ? $"{what} accepted." : $"{what} refused: {refusal}.";
    }

    /// <summary>
    /// ADR-013 point 5's last step: GitHub has not stopped the run in the <see cref="StaleRun.StopWait"/> since the
    /// force-cancel, so every test on the target is blocked until it stops or someone deletes it (R-24). The hidden marker
    /// keys the alert to the check run, so the cycles that keep force-cancelling do not repeat it.
    /// </summary>
    async Task Unstoppable(Target target, CheckRun check, long runId, DateTimeOffset asked, DateTimeOffset forced, CancellationToken ct)
    {
        var repo = target.Repo;
        var title = $"Target run could not be stopped on {repo}";
        if (alerts is null) { AlertFailures.Add($"{title}: no alert sink configured"); return; }
        try
        {
            await alerts.Raise(title,
                $"[Target run {runId}](https://github.com/{repo}/actions/runs/{runId}) of `{repo}`, testing "
                + $"{Markdown.Commit(repo, check.Sha)}, passed its deadline and was cancelled at {Markers.Stamp(asked)} and "
                + $"force-cancelled at {Markers.Stamp(forced)}. GitHub has still not stopped it.\n\n"
                + $"Check run {check.Id} stays `in_progress`, so **no test starts for `{repo}`** — not a retry, not a newer "
                + "head and not a forced dispatch — for as long as this lasts (ADR-013). Main Watcher repeats the force-cancel "
                + "on every cycle. As soon as the run stops, its `main-watcher` job is judged like any other. To release the "
                + "target by hand, cancel the run yourself; GitHub refuses to delete a run that is still going, so delete it "
                + "afterwards only if its steps should not be judged at all, which gives \"outcome unknown\".",
                ct, $"<!-- main-watcher unstoppable check={check.Id} -->");
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { AlertFailures.Add($"{title}: {e.Message}"); }
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
            var open = (await github.OpenIssues(repo, Reporter.LockLabel, ct)).Where(IsApp).OrderBy(i => i.Number).ToArray();
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

    /// <summary>Only the App's own locks count, matching the gate and the Reporter.</summary>
    bool IsApp(Issue issue) => issue.Author == botLogin && issue.AuthorType == "Bot";

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
