using System.Text.Json.Nodes;
using MainWatcher.Core;
using MainWatcher.Scenarios.Infra;
using MainWatcher.Scenarios.Sandbox;

namespace MainWatcher.Scenarios.Scenarios;

/// <summary>Steps the outage units share.</summary>
static class OutageSteps
{
    /// <summary>Opens a real lock: a failing push, tested and reported while the worker still runs.</summary>
    public static async Task<Issue> Lock(ScenarioContext ctx, Action<JsonObject>? more = null)
    {
        var since = DateTimeOffset.UtcNow;
        var sha = await ctx.Push("fail Alpha", s =>
        {
            s["failing_tests"] = new JsonArray("Alpha");
            more?.Invoke(s);
        });
        await ctx.Result(sha, "failure");
        var issue = await ctx.Target.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Pass($"a failure opened lock #{issue.Number}");
        return issue;
    }

    /// <summary>Waits, with the watcher down, for a lock's lease to run out, and returns the lease it had.</summary>
    public static async Task<DateTimeOffset> Lapse(ScenarioContext ctx, Issue issue)
    {
        var current = await ctx.Target.Issue(issue.Number, ctx.Ct);
        var lease = Markers.Time(current.Body, "lease_until") ?? throw new ScenarioFailure($"lock #{issue.Number} has no lease");
        ctx.Step($"waiting for lock #{issue.Number}'s lease to run out at {lease:HH:mm:ss}Z");
        await Poll.Till(lease.AddSeconds(30), ctx.Ct);
        ctx.Require(Markers.Time((await ctx.Target.Issue(issue.Number, ctx.Ct)).Body, "lease_until") == lease,
            $"nothing renewed lock #{issue.Number}'s lease while the watcher was down");
        return lease;
    }

    /// <summary>Queues an unlabelled pull request and waits for it to merge through a gate that failed open.</summary>
    public static async Task<PullRequest> MergeThroughLapse(ScenarioContext ctx, string name, int lockNumber,
        IReadOnlyDictionary<string, string?>? files = null)
    {
        var pr = await ctx.Target.OpenPullRequest(name, ctx.Ct, files: files);
        var queued = DateTimeOffset.UtcNow;
        await ctx.Target.Enqueue(pr, ctx.Ct);
        await ctx.Target.AwaitMerged(pr, TimeSpan.FromMinutes(8), ctx.Ct);
        var (run, log) = await GateLog.Verdict(ctx, pr, queued);
        ctx.Require(GateLog.Has(log, "The lock is open but its lease is missing, unreadable, expired") && GateLog.Has(log, $"#{lockNumber} (lease_until=")
            && await GateLog.FailedOpen(ctx, run), $"unlabelled #{pr.Number} merged: the gate warned LOCK LEASE EXPIRED and failed open");
        return pr;
    }

    /// <summary>Waits for a lock to report a merge made while it was open, and for the alert naming it.</summary>
    public static async Task Reported(ScenarioContext ctx, int lockNumber, int pr, DateTimeOffset since)
    {
        var key = $"merged_while_locked pr={pr}";
        await Poll.True($"lock #{lockNumber} to report #{pr}", TimeSpan.FromMinutes(10), async () =>
            (await ctx.Target.Issue(lockNumber, ctx.Ct)).Body.Contains(key, StringComparison.Ordinal), ctx.Ct);
        var alert = await ctx.Replica.AwaitAlert($"Merged while locked on {ctx.Target.Repo}", since, TimeSpan.FromMinutes(3), ctx.Ct);
        await Poll.True($"alert #{alert.Number} to name #{pr}", TimeSpan.FromMinutes(3), async () =>
            (await ctx.Replica.AlertText(alert, ctx.Ct)).Contains(key, StringComparison.Ordinal), ctx.Ct);
        ctx.Pass($"#{pr}'s merge was reported on lock #{lockNumber} and in alert #{alert.Number}");
    }
}

/// <summary>
/// TS-S7 (a): with the watcher down and no lock, a merge proceeds at once. TS-S11: that merge, a new head no cycle tests, is
/// tested by the next sweep, which raises "trigger worker appears down".
/// </summary>
public sealed class WorkerDown : Scenario
{
    public override string[] Covers => ["TS-S7 (a)", "TS-S11"];
    public override string Title => "With the worker down: a merge proceeds with no lock, and the sweep tests it and says the worker is down";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(30);
    public override Phase Phase => Phase.Outage;

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        await ctx.Sandbox.Outage.Begin(Name);
        var began = ctx.Sandbox.Outage.Began!.Value;
        var pr = await t.OpenPullRequest("ts-s7a", ctx.Ct);
        var queued = DateTimeOffset.UtcNow;
        await t.Enqueue(pr, ctx.Ct);
        var merged = await t.AwaitMerged(pr, TimeSpan.FromMinutes(8), ctx.Ct);
        var (_, log) = await GateLog.Verdict(ctx, pr, queued);
        ctx.Require(GateLog.Has(log, "No open `main-broken` issue"), $"TS-S7 (a): with the watcher down and no lock, #{pr.Number} merged at once");
        var head = await t.Head(ctx.Ct);

        // The sweep says the worker is down only for work that has waited 15 minutes (ADR-010).
        ctx.Step($"leaving {Target.Short(head)} untested until 16 minutes after the merge");
        await Poll.Till(merged.AddMinutes(16), ctx.Ct);
        ctx.Require((await ctx.Replica.Cycles(t, began, ctx.Ct)).Count == 0 && (await t.Checks(head, ctx.Ct)).Count == 0,
            "TS-S11: no cycle ran for the target, and the merge stayed untested, while the worker was down");
        var sweep = await ctx.Sandbox.Outage.End(Name);

        var sweepLog = await ctx.Replica.CycleLog(sweep, t, ctx.Ct);
        ctx.Require(sweepLog.Contains($"Sweep: the trigger worker appears down; head {Target.Short(head)} is eligible", StringComparison.Ordinal)
            && sweepLog.Contains("Started check", StringComparison.Ordinal), $"TS-S11: sweep run {sweep} found the waiting head and started its test");
        var alert = await ctx.Replica.AwaitAlert($"Trigger worker appears down (work waiting on {t.Repo})", began, TimeSpan.FromMinutes(2), ctx.Ct);
        ctx.Require(alert.Body.Contains("has waited", StringComparison.Ordinal), $"TS-S11: alert #{alert.Number} says the worker appears down");
        var check = (await t.Checks(head, ctx.Ct)).FirstOrDefault() ?? throw new ScenarioFailure("the sweep started no check run");
        var done = await t.AwaitCompleted(check.Id, TimeSpan.FromMinutes(12), ctx.Ct);
        ctx.Require(done.Conclusion == "success", $"TS-S11: the sweep's test of {Target.Short(head)} was reported once the worker was back ({done})");
    }
}

/// <summary>
/// TS-S7 (b): starting from an open lock, with the watcher down, merges proceed once <c>lock_lease</c> has passed, with the
/// gate warning "LOCK LEASE EXPIRED". Restored, the watcher renews the lease, records the lapse, alerts, reports the merge,
/// and the lock is enforced again.
/// </summary>
public sealed class LeaseLapse : Scenario
{
    public override string[] Covers => ["TS-S7 (b)"];
    public override string Title => "A lock whose lease lapses in an outage lets merges through, and is renewed and enforced after it";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(35);
    public override Phase Phase => Phase.Outage;

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var lockIssue = await OutageSteps.Lock(ctx);
        await ctx.Sandbox.Outage.Begin(Name);
        var began = ctx.Sandbox.Outage.Began!.Value;
        var lapsed = await OutageSteps.Lapse(ctx, lockIssue);
        var pr = await OutageSteps.MergeThroughLapse(ctx, "ts-s7b", lockIssue.Number);
        await ctx.Sandbox.Outage.End(Name);

        var renewed = await Poll.Until($"lock #{lockIssue.Number}'s lease to be renewed", TimeSpan.FromMinutes(8), async () =>
            await t.Issue(lockIssue.Number, ctx.Ct) is { } issue && Markers.Time(issue.Body, "lease_until") > DateTimeOffset.UtcNow
                && Markers.Field(issue.Body, "lapsed") is not null ? issue : null, ctx.Ct);
        ctx.Pass($"the watcher renewed the lease and recorded the lapse (lapsed={Markers.Field(renewed.Body, "lapsed")})");
        ctx.Require(Markers.Field(renewed.Body, "lapsed")!.StartsWith(Markers.Stamp(lapsed), StringComparison.Ordinal),
            "the recorded lapse starts when the lease ran out");
        await Poll.True($"the lapse comment on #{lockIssue.Number}", TimeSpan.FromMinutes(3), async () =>
            (await t.Comments(lockIssue.Number, ctx.Ct)).Any(c => c.Body.Contains("This lock's lease ran out at", StringComparison.Ordinal)), ctx.Ct);
        ctx.Pass("the lock says when its lease ran out and was renewed");
        var alert = await ctx.Replica.AwaitAlert($"Lock lease lapsed on {t.Repo}", began, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Pass($"alert #{alert.Number} says the lease lapsed");
        await OutageSteps.Reported(ctx, lockIssue.Number, pr.Number, began);

        var again = await t.OpenPullRequest("ts-s7b-after", ctx.Ct);
        var queued = DateTimeOffset.UtcNow;
        await t.Enqueue(again, ctx.Ct);
        await t.AwaitRemovedFromQueue(again, TimeSpan.FromMinutes(8), ctx.Ct);
        var (run, log) = await GateLog.Verdict(ctx, again, queued);
        ctx.Require(run.Conclusion == "failure" && GateLog.Has(log, $"`main` is locked by #{lockIssue.Number}"),
            $"the lock is enforced again: unlabelled #{again.Number} was removed from the queue");
    }
}

/// <summary>
/// TS-S15: unlabelled PRs merged during a lock, then a person closing the lock before the watcher recovers, are reported on
/// recovery: on the closed issue, as comments and as an alert, and the issue is marked <c>reconciled=complete</c>. (b) One PR
/// is labelled <c>fixes-main</c> only after it merged, and is still reported.
/// </summary>
public sealed class ClosedBeforeRecovery : Scenario
{
    public override string[] Covers => ["TS-S15"];
    public override string Title => "Merges during a lock closed by hand before recovery are still reported, labelled late or not";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(35);
    public override Phase Phase => Phase.Outage;

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var lockIssue = await OutageSteps.Lock(ctx);
        await ctx.Sandbox.Outage.Begin(Name);
        var began = ctx.Sandbox.Outage.Began!.Value;
        await OutageSteps.Lapse(ctx, lockIssue);
        var plain = await OutageSteps.MergeThroughLapse(ctx, "ts-s15a", lockIssue.Number);
        var late = await OutageSteps.MergeThroughLapse(ctx, "ts-s15b", lockIssue.Number);
        await t.AddLabels(late.Number, [SandboxOrg.FixLabel], ctx.Ct);
        ctx.Step($"labelled #{late.Number} fixes-main after it merged (TS-S15 (b))");
        await t.CloseIssue(lockIssue.Number, ctx.Ct);
        ctx.Step($"closed lock #{lockIssue.Number} by hand");
        await ctx.Sandbox.Outage.End(Name);

        var done = await Poll.Until($"lock #{lockIssue.Number} to be reconciled", TimeSpan.FromMinutes(10), async () =>
            await t.Issue(lockIssue.Number, ctx.Ct) is { } issue && Markers.Field(issue.Body, "reconciled") == "complete" ? issue : null, ctx.Ct);
        ctx.Require(done.State == "closed", $"lock #{lockIssue.Number} stayed closed and was marked reconciled=complete");
        ctx.Require(done.Body.Contains("**Merged while locked**", StringComparison.Ordinal)
            && done.Body.Contains($"pr={plain.Number}", StringComparison.Ordinal) && done.Body.Contains($"pr={late.Number}", StringComparison.Ordinal),
            $"its body lists both merges, #{plain.Number} and #{late.Number}");
        var comments = await t.Comments(lockIssue.Number, ctx.Ct);
        ctx.Require(comments.Count(c => c.Body.Contains("The lock has closed since, so this is a record rather than a block", StringComparison.Ordinal)) == 2,
            "with a comment for each merge");
        ctx.Require(comments.Any(c => c.Body.Contains("closed this lock by hand", StringComparison.Ordinal)), "and the override comment");
        await OutageSteps.Reported(ctx, lockIssue.Number, plain.Number, began);
        await OutageSteps.Reported(ctx, lockIssue.Number, late.Number, began);
        ctx.Pass($"TS-S15 (b): #{late.Number}, labelled fixes-main only after it merged, was reported all the same");
    }
}

/// <summary>
/// TS-S17 (b): a group passes the gate during a lease lapse; the Planner renews the lease and is stopped before sweeping, with
/// an older <c>queue_swept</c> marker present: the next watcher run still re-runs that group's gate. A group that merged
/// before any re-run is reported.
/// </summary>
public sealed class SweepAfterRenewal : Scenario
{
    const int SlowCheck = 15;

    public override string[] Covers => ["TS-S17 (b)"];
    public override string Title => "A renewal stopped before its queue sweep still leaves the sweep owed, and the next run re-runs the gate";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(40);
    public override Phase Phase => Phase.Outage;

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        await ctx.Result(await ctx.Push($"a {SlowCheck}-minute second check", s => s["slow_check_minutes"] = SlowCheck), "success");
        var lockIssue = await OutageSteps.Lock(ctx);
        var swept = await Poll.Value($"lock #{lockIssue.Number}'s first queue sweep", TimeSpan.FromMinutes(5), async () =>
            Markers.Time((await t.Issue(lockIssue.Number, ctx.Ct)).Body, "queue_swept"), ctx.Ct);
        ctx.Pass($"lock #{lockIssue.Number} carries queue_swept={Markers.Stamp(swept)}");
        await ctx.Sandbox.Outage.Begin(Name);
        var began = ctx.Sandbox.Outage.Began!.Value;
        await OutageSteps.Lapse(ctx, lockIssue);

        // A group that merges during the lapse, reported later. Its branch turns the slow check off so it merges at once,
        // and main's setting is put back straight after.
        var merged = await OutageSteps.MergeThroughLapse(ctx, "ts-s17b-merged", lockIssue.Number,
            new Dictionary<string, string?> { [Target.SwitchesFile] = await SwitchesWith(ctx, 0) });
        await t.Commit(new Dictionary<string, string?> { [Target.SwitchesFile] = await SwitchesWith(ctx, SlowCheck) }, "slow check back on", ctx.Ct);

        var waiting = await t.OpenPullRequest("ts-s17b-queued", ctx.Ct);
        var queued = DateTimeOffset.UtcNow;
        await t.Enqueue(waiting, ctx.Ct);
        var (run, log) = await GateLog.Verdict(ctx, waiting, queued);
        ctx.Require(run.Conclusion == "success" && GateLog.Has(log, "lease is missing, unreadable, expired"),
            $"#{waiting.Number}'s group passed the gate during the lapse and waits on its {SlowCheck}-minute check");

        await ctx.Replica.SetSwitch(Replica.ExitAfter, t, ["renew"], ctx.Ct);
        long cycle;
        try
        {
            cycle = await ctx.Sandbox.Outage.Dispatch(t);
            var stopped = await ctx.Replica.AwaitRun(cycle, TimeSpan.FromMinutes(10), ctx.Ct);
            ctx.Require(stopped.Conclusion == "failure" && (await ctx.Replica.CycleLog(cycle, t, ctx.Ct))
                .Contains("MW_SANDBOX_EXIT_AFTER: exiting after the renew write.", StringComparison.Ordinal),
                $"cycle {cycle} renewed the lease and was stopped before its queue sweep");
        }
        finally { await ctx.Replica.SetSwitch(Replica.ExitAfter, t, [], ctx.Ct); }
        var body = (await t.Issue(lockIssue.Number, ctx.Ct)).Body;
        var required = Markers.Time(body, "sweep_required");
        var sweptAt = Markers.Time(body, "queue_swept");
        ctx.Require(required > sweptAt, "the lock now owes a sweep: sweep_required is later than the older queue_swept",
            $"sweep_required={required:O}, queue_swept={sweptAt:O}");
        await ctx.Sandbox.Outage.End(Name);

        await t.AwaitRemovedFromQueue(waiting, TimeSpan.FromMinutes(10), ctx.Ct);
        (run, log) = await GateLog.Verdict(ctx, waiting, queued, attempt: 2);
        ctx.Require(run.Attempt >= 2 && run.Conclusion == "failure" && GateLog.Has(log, $"Not labelled: #{waiting.Number}"),
            $"the next watcher run re-ran #{waiting.Number}'s gate, which failed, and the queue removed it before its slow check ended");
        await OutageSteps.Reported(ctx, lockIssue.Number, merged.Number, began);
    }

    static async Task<string> SwitchesWith(ScenarioContext ctx, int slowCheck)
    {
        var switches = await ctx.Target.Switches(ctx.Ct);
        switches["slow_check_minutes"] = slowCheck;
        return Templates.Format(switches);
    }
}
