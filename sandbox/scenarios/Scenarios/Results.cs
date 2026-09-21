using System.Text.Json.Nodes;
using MainWatcher.Core;
using MainWatcher.Scenarios.Infra;
using MainWatcher.Scenarios.Sandbox;

namespace MainWatcher.Scenarios.Scenarios;

/// <summary>Steps the TS-S16 units share.</summary>
static class ResultSteps
{
    /// <summary>One commit with new switches and, when given, a new caller workflow.</summary>
    public static Task<string> Push(ScenarioContext ctx, string message, Action<JsonObject> edit, string? caller = null) =>
        ctx.Push(message, edit, caller is null ? null : new Dictionary<string, string?> { [Target.CallerFile] = caller });

    public static JsonArray Tests(params string[] names) => new(names.Select(n => (JsonNode)n).ToArray());

    /// <summary>Waits for the first check run on a head to complete, and returns it.</summary>
    public static async Task<CheckRun> Judged(ScenarioContext ctx, string sha, int minutes = 14)
    {
        var check = await ctx.Tested(sha);
        return await ctx.Target.AwaitCompleted(check.Id, TimeSpan.FromMinutes(minutes), ctx.Ct);
    }

    public static async Task NoLockSince(ScenarioContext ctx, DateTimeOffset since, string why) =>
        ctx.Require((await ctx.Target.Locks(ctx.Ct)).All(l => l.CreatedAt < since.AddSeconds(-5)), $"no lock was opened: {why}");
}

/// <summary>
/// TS-S16 (b) a failing restore is neutral with an alert and no lock, and twice raises "twice in a row"; (c) a workflow with
/// the test step renamed is "outcome contract broken"; (a) a failing run whose upload fails opens a lock, failing tests unknown.
/// </summary>
public sealed class BrokenRuns : Scenario
{
    public override string[] Covers => ["TS-S16 (a)", "TS-S16 (b)", "TS-S16 (c)"];
    public override string Title => "Restore failures are neutral, a renamed test step breaks the contract, a lost upload still locks";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(24);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var r = ctx.Replica;
        var since = DateTimeOffset.UtcNow;

        var restore = await ResultSteps.Push(ctx, "restore fails", s => s["fail_restore"] = true);
        var first = await ResultSteps.Judged(ctx, restore);
        ctx.Require(first.Conclusion == "neutral" && first.Title == "Infrastructure error"
            && first.Summary.StartsWith("Infrastructure error: the tests did not finish.", StringComparison.Ordinal),
            "(b) a failing restore completed the check run neutral: infrastructure error", first.ToString());
        await r.AwaitAlert($"Infrastructure error on {t.Repo}", since, TimeSpan.FromMinutes(2), ctx.Ct);
        var second = await t.AwaitCompleted((await t.AwaitCheck(restore, TimeSpan.FromMinutes(6), ctx.Ct, 2)).Id, TimeSpan.FromMinutes(10), ctx.Ct);
        ctx.Require(second.Conclusion == "neutral", "(b) the retest failed its restore too");
        await r.AwaitAlert($"Infrastructure errors twice in a row on {t.Repo}", since, TimeSpan.FromMinutes(2), ctx.Ct);
        ctx.Pass("(b) \"Infrastructure error\" and then \"Infrastructure errors twice in a row\" were raised");
        await ResultSteps.NoLockSince(ctx, since, "(b) a restore failure is not a test failure");

        var renamed = await ResultSteps.Push(ctx, "fail Alpha, on the workflow with its test step renamed", s =>
        {
            s["fail_restore"] = false;
            s["failing_tests"] = ResultSteps.Tests("Alpha");
        }, t.Caller("ts-s16-renamed-step"));
        var broken = await ResultSteps.Judged(ctx, renamed);
        ctx.Require(broken.Conclusion == "neutral" && broken.Title == "Outcome contract broken"
            && broken.Summary.Contains("succeeded without a `main-watcher-test` result", StringComparison.Ordinal),
            "(c) with the step renamed, the Reporter reported a broken contract, not a red result", broken.ToString());
        await r.AwaitAlert($"Outcome contract broken on {t.Repo}", since, TimeSpan.FromMinutes(2), ctx.Ct);
        ctx.Pass("(c) \"Outcome contract broken\" was raised");
        await ResultSteps.NoLockSince(ctx, since, "(c) although Alpha failed");

        var lost = await ResultSteps.Push(ctx, "fail Alpha, and lose the upload", s => s["fail_upload"] = true, t.Caller());
        var red = await ResultSteps.Judged(ctx, lost);
        ctx.Require(red.Conclusion == "failure", "(a) with its upload failed, the failing run is still red", red.ToString());
        ctx.Require((await t.Artifact(red.RunId!.Value, "main-watcher-ctrf", ctx.Ct)).Count == 0, "(a) the run uploaded no CTRF artifact");
        var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Require(lockIssue.Body.Contains("failing tests unknown", StringComparison.Ordinal),
            $"(a) lock #{lockIssue.Number} opened, saying failing tests unknown");
    }
}

/// <summary>
/// TS-S16 (d): a failing run whose upload then hangs past its timeout, and one cancelled by hand after its tests finished, both
/// still lock; a run cancelled during its test step is neutral.
/// </summary>
public sealed class LateFailures : Scenario
{
    public override string[] Covers => ["TS-S16 (d)"];
    public override string Title => "Failures survive a hung upload and a late cancel; a cancel during the tests is neutral";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(26);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var since = DateTimeOffset.UtcNow;

        var hung = await ResultSteps.Push(ctx, "fail Beta, upload hangs", s =>
        {
            s["failing_tests"] = ResultSteps.Tests("Beta");
            s["hang_upload"] = true;
        });
        var first = await ResultSteps.Judged(ctx, hung, 16);
        var job = await t.TestJob(first.RunId!.Value, ctx.Ct);
        ctx.Require(first.Conclusion == "failure" && job?.StepNamed("sandbox upload switches")?.Conclusion == "failure",
            "a failing run whose upload step hung past its timeout was still red", job?.ToString());
        var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Pass($"and opened lock #{lockIssue.Number}");

        var late = await ResultSteps.Push(ctx, "fail Gamma, upload hangs", s => s["failing_tests"] = ResultSteps.Tests("Gamma"));
        var second = await ctx.Tested(late);
        await t.AwaitTestJob(second.RunId!.Value, "the upload to hang", j => j.StepNamed("sandbox upload switches")?.Status == "in_progress",
            TimeSpan.FromMinutes(8), ctx.Ct, TimeSpan.FromSeconds(10));
        await t.CancelRun(second.RunId.Value, ctx.Ct);
        ctx.Step($"cancelled target run {second.RunId} after its tests had finished");
        second = await t.AwaitCompleted(second.Id, TimeSpan.FromMinutes(12), ctx.Ct);
        ctx.Require(second.Conclusion == "failure", "a failing run cancelled after its tests finished was still red", second.ToString());
        await Poll.True($"lock #{lockIssue.Number}'s comment for check {second.Id}", TimeSpan.FromMinutes(3), async () =>
            (await t.Comments(lockIssue.Number, ctx.Ct)).Any(c => c.Body.Contains($"check={second.Id}", StringComparison.Ordinal)), ctx.Ct);
        ctx.Pass($"and was added to lock #{lockIssue.Number}");

        var alertsFrom = DateTimeOffset.UtcNow;
        var hanging = await ResultSteps.Push(ctx, "a test hangs", s =>
        {
            s["failing_tests"] = ResultSteps.Tests();
            s["hang_upload"] = false;
            s["hang_test"] = true;
        });
        var third = await ctx.Tested(hanging);
        await t.AwaitTestJob(third.RunId!.Value, "the tests to be running", j => j.StepNamed("main-watcher-test")?.Status == "in_progress",
            TimeSpan.FromMinutes(8), ctx.Ct, TimeSpan.FromSeconds(10));
        await ctx.Wait(TimeSpan.FromSeconds(20));
        await t.CancelRun(third.RunId.Value, ctx.Ct);
        ctx.Step($"cancelled target run {third.RunId} during its test step");
        third = await t.AwaitCompleted(third.Id, TimeSpan.FromMinutes(10), ctx.Ct);
        ctx.Require(third.Conclusion == "neutral", "a run cancelled during its test step was neutral", third.ToString());
        await ctx.Replica.AwaitAlert($"Infrastructure error on {t.Repo}", alertsFrom, TimeSpan.FromMinutes(2), ctx.Ct);
        ctx.Require((await t.Comments(lockIssue.Number, ctx.Ct)).All(c => !c.Body.Contains($"check={third.Id}", StringComparison.Ordinal)),
            "with an infrastructure alert, and nothing added to the lock");
    }
}

/// <summary>
/// TS-S16 (e): a test that hangs past the target's timeout is neutral with an infrastructure alert, not a lock, whether the
/// wrapper's deadline or a <c>timeout-minutes</c> on the step ends it, and the finished marker is skipped either way.
/// </summary>
public sealed class HungTests : Scenario
{
    public override string[] Covers => ["TS-S16 (e)"];
    public override string Title => "A test hanging past the target's timeout is neutral, with or without a step timeout";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(18);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var since = DateTimeOffset.UtcNow;
        await ctx.Replica.EditTarget(t, e => e with { Timeout = 2 }, ctx.Ct);
        foreach (var (variant, how) in new[] { ("main", "the wrapper's deadline"), ("ts-s16-step-timeout", "the step's timeout-minutes") })
        {
            var alertsFrom = DateTimeOffset.UtcNow;
            var sha = await ResultSteps.Push(ctx, $"a test hangs, 2-minute timeout, on {variant}", s => s["hang_test"] = true,
                t.Caller(variant, timeoutMinutes: 2));
            var check = await ResultSteps.Judged(ctx, sha, 12);
            var job = await t.TestJob(check.RunId!.Value, ctx.Ct);
            ctx.Require(check.Conclusion == "neutral" && check.Title == "Infrastructure error"
                && job?.StepNamed("main-watcher-test")?.Conclusion == "failure" && job.StepNamed("main-watcher-tests-finished")?.Conclusion is "skipped" or null,
                $"ended by {how}: the test step failed, the finished marker was skipped, and the result was neutral", $"{check}; {job}");
            await ctx.Replica.AwaitAlert($"Infrastructure error on {t.Repo}", alertsFrom, TimeSpan.FromMinutes(2), ctx.Ct);
            ctx.Pass($"ended by {how}: an infrastructure alert was raised");
        }
        await ResultSteps.NoLockSince(ctx, since, "a hung test is not a failing one");
    }
}

/// <summary>
/// TS-S16 (f): a failing run whose <c>report</c> job waits for a runner beyond the stale threshold opens a lock as soon as the
/// <c>main-watcher</c> job completes, and is never marked stale.
/// </summary>
public sealed class StuckReportJob : Scenario
{
    public override string[] Covers => ["TS-S16 (f)"];
    public override string Title => "A failing run whose report job never gets a runner locks at once, and is never marked stale";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(34);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var since = DateTimeOffset.UtcNow;
        var sha = await ResultSteps.Push(ctx, "fail Alpha, report job stuck", s => s["failing_tests"] = ResultSteps.Tests("Alpha"),
            t.Caller("ts-s16-report-stuck"));
        var check = await ResultSteps.Judged(ctx, sha);
        var jobs = await t.Jobs(check.RunId!.Value, ctx.Ct) ?? [];
        var report = jobs.Single(j => j.Name.EndsWith("/ report", StringComparison.Ordinal));
        ctx.Require(check.Conclusion == "failure" && !report.Started,
            "the check run completed as failure while the report job was still waiting for a runner", $"{check}; {report}");
        var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Pass($"lock #{lockIssue.Number} opened");

        ctx.Step("waiting until the run is past the 30-minute queue deadline");
        await Poll.Till(check.StartedAt.AddMinutes(StaleRun.DefaultQueueDeadline.TotalMinutes + 2), ctx.Ct);
        var after = await t.Check(check.Id, ctx.Ct);
        report = (await t.Jobs(check.RunId.Value, ctx.Ct) ?? []).Single(j => j.Name.EndsWith("/ report", StringComparison.Ordinal));
        ctx.Require(!report.Started && after.Conclusion == "failure" && after.CompletedAt == check.CompletedAt
            && !after.Summary.Contains(StaleRun.CancelRequested, StringComparison.Ordinal),
            "32 minutes on, with the report job still queued, the check run was unchanged and never marked stale", $"{after}; {report}");
        ctx.Require((await t.Checks(sha, ctx.Ct)).Count == 1 && (await t.Comments(lockIssue.Number, ctx.Ct)).Count == 0,
            "no retest and no further lock comment");
    }
}

/// <summary>
/// TS-S16 (g), with this target's 10-minute queue deadline: a job that starts late, but within the deadline, and fails after
/// its check run is 20 minutes old still locks; a job that never gets a runner is cancelled at the deadline and is neutral,
/// and no test of a newer head starts before the cancelled run has stopped.
/// </summary>
public sealed class QueueDeadline : Scenario
{
    public override string[] Covers => ["TS-S16 (g)"];
    public override string Title => "The queue deadline: a late start still locks; a job with no runner is cancelled, neutral, before anything newer";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(40);
    public override Target? Pinned(SandboxOrg sandbox) => sandbox.QueueDeadlineTarget;

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var since = DateTimeOffset.UtcNow;

        // A job that waits for the sandbox-hold concurrency group, held for 9 minutes, then runs a 12-minute failing suite.
        var hold = await t.Dispatch("sandbox-hold.yml", new Dictionary<string, string> { ["minutes"] = "9" }, ctx.Ct);
        await Poll.True("the hold to start", TimeSpan.FromMinutes(5), async () =>
            (await t.Jobs(hold, ctx.Ct) ?? []).Any(j => j.Status == "in_progress"), ctx.Ct, TimeSpan.FromSeconds(10));
        var late = await ResultSteps.Push(ctx, "fail Alpha, 12-minute suite, test job held", s =>
        {
            s["failing_tests"] = ResultSteps.Tests("Alpha");
            s["slow_suite_minutes"] = 12;
        }, t.Caller("ts-s16-held-job"));
        var check = await ctx.Tested(late);
        var job = await t.AwaitTestJob(check.RunId!.Value, "the held job to start", j => j.Started, TimeSpan.FromMinutes(12), ctx.Ct);
        var startedAfter = (job.StartedAt ?? DateTimeOffset.UtcNow) - check.StartedAt;
        ctx.Require(startedAfter > TimeSpan.FromMinutes(5) && startedAfter < TimeSpan.FromMinutes(SandboxOrg.ShortQueueDeadline),
            $"the job got its runner {startedAfter.TotalMinutes:0.0} min after its check run, inside the {SandboxOrg.ShortQueueDeadline}-minute queue deadline");
        var red = await t.AwaitCompleted(check.Id, TimeSpan.FromMinutes(25), ctx.Ct);
        ctx.Require(red.Conclusion == "failure" && red.CompletedAt - red.StartedAt > TimeSpan.FromMinutes(20)
            && !red.Summary.Contains(StaleRun.CancelRequested, StringComparison.Ordinal),
            $"it failed {(red.CompletedAt!.Value - red.StartedAt).TotalMinutes:0} min after its check run was created, was never cancelled, and was red", red.ToString());
        var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Pass($"and opened lock #{lockIssue.Number}");

        var stranded = await ResultSteps.Push(ctx, "pass, but on a runner label no runner has", s =>
        {
            s["failing_tests"] = ResultSteps.Tests();
            s["slow_suite_minutes"] = 0;
        }, t.Caller(runsOn: "sandbox-no-such-runner"));
        var waiting = await ctx.Tested(stranded);
        await ctx.Wait(TimeSpan.FromMinutes(1));
        var newer = await ResultSteps.Push(ctx, "pass, on the usual runner", _ => { }, t.Caller());
        var stopping = await Poll.Until("the Planner to cancel the stranded run", TimeSpan.FromMinutes(SandboxOrg.ShortQueueDeadline + 4), async () =>
            await t.Check(waiting.Id, ctx.Ct) is { } c && c.Summary.Contains(StaleRun.CancelRequested, StringComparison.Ordinal) ? c : null, ctx.Ct);
        ctx.Require(stopping.Status == "in_progress" && stopping.Title == "Stopping a stale target run"
            && Markers.Time(stopping.Summary, StaleRun.CancelRequested) >= waiting.StartedAt.AddMinutes(SandboxOrg.ShortQueueDeadline),
            "at the queue deadline the Planner cancelled the run and kept its check run in progress", stopping.ToString());
        ctx.Require((await t.Checks(newer, ctx.Ct)).Count == 0, "no test of the newer head had started");
        var neutral = await t.AwaitCompleted(waiting.Id, TimeSpan.FromMinutes(10), ctx.Ct);
        ctx.Require(neutral.Conclusion == "neutral" && neutral.Summary.Contains("Main Watcher cancelled this run at", StringComparison.Ordinal),
            "once the run stopped, its check run was neutral, saying Main Watcher cancelled it", neutral.ToString());
        var next = await t.AwaitCheck(newer, TimeSpan.FromMinutes(5), ctx.Ct);
        ctx.Require(next.StartedAt >= neutral.CompletedAt!.Value.AddSeconds(-2), "the newer head's test started only after the cancelled run was judged");
        ctx.Require((await t.Comments(lockIssue.Number, ctx.Ct)).All(c => !c.Body.Contains($"check={waiting.Id}", StringComparison.Ordinal)),
            "the neutral result added nothing to the lock");
    }
}

/// <summary>
/// TS-S16 (h), first half: a failing test whose marker step succeeds, followed by a job that never completes, is cancelled at
/// its run deadline and then opens a lock.
/// </summary>
public sealed class RunDeadline : Scenario
{
    public override string[] Covers => ["TS-S16 (h) run deadline"];
    public override string Title => "A job that never ends is cancelled at its run deadline, and its finished failure still locks";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(45);
    public override Phase Phase => Phase.Early;

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var since = DateTimeOffset.UtcNow;
        var (check, started) = await NeverEnding.Start(ctx);
        var stopping = await NeverEnding.AtDeadline(ctx, check, started);
        ctx.Require(stopping.Status == "in_progress", "at the run deadline the Planner cancelled the run, keeping its check run in progress");
        var red = await t.AwaitCompleted(check.Id, TimeSpan.FromMinutes(15), ctx.Ct);
        ctx.Require(red.Conclusion == "failure" && red.Summary.Contains("Main Watcher cancelled this run at", StringComparison.Ordinal),
            "once stopped, the run was judged by its steps: red, saying Main Watcher cancelled it", red.ToString());
        var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Require(lockIssue.Body.Contains("failing tests unknown", StringComparison.Ordinal), $"lock #{lockIssue.Number} opened, failing tests unknown");
    }
}

/// <summary>
/// TS-S16 (h), second half: with cancel and force-cancel made to fail, the check run stays in progress, a "target run could not
/// be stopped" alert is raised, and no test starts for a newer head until the run is deleted.
/// </summary>
public sealed class UnstoppableRun : Scenario
{
    public override string[] Covers => ["TS-S16 (h) unstoppable"];
    public override string Title => "A run that cannot be stopped blocks the target, with an alert, until it is deleted";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(80);
    public override Phase Phase => Phase.Early;

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var r = ctx.Replica;
        var since = DateTimeOffset.UtcNow;
        await r.SetSwitch(Replica.RefuseCancel, t, ["true"], ctx.Ct);
        try
        {
            var (check, started) = await NeverEnding.Start(ctx);
            var newer = await ctx.Push("pass, a newer head", s =>
            {
                foreach (var (key, value) in ctx.Sandbox.Templates.DefaultSwitches()) s[key] = value?.DeepClone();
            }, new Dictionary<string, string?> { [Target.CallerFile] = t.Caller() });
            var refused = await NeverEnding.AtDeadline(ctx, check, started);
            var cancelAt = Markers.Time(refused.Summary, StaleRun.CancelRequested)!.Value;
            var forced = await Poll.Until("the Planner to force-cancel", TimeSpan.FromMinutes(25), async () =>
                await t.Check(check.Id, ctx.Ct) is { } c && Markers.Time(c.Summary, StaleRun.ForceCancelRequested) is not null ? c : null, ctx.Ct);
            ctx.Require(forced.Status == "in_progress" && Markers.Time(forced.Summary, StaleRun.CancelRequested) == cancelAt
                && Markers.Time(forced.Summary, StaleRun.ForceCancelRequested) >= cancelAt.Add(StaleRun.StopWait),
                "the refused cancel was followed, 15 minutes on, by a force-cancel, with the check run still in progress");
            var alert = await r.AwaitAlert($"Target run could not be stopped on {t.Repo}", since, TimeSpan.FromMinutes(25), ctx.Ct);
            ctx.Require(alert.Body.Contains($"unstoppable check={check.Id}", StringComparison.Ordinal),
                $"15 minutes after the refused force-cancel, alert #{alert.Number} said the run could not be stopped");
            ctx.Require((await t.Check(check.Id, ctx.Ct)).Status == "in_progress" && (await t.Checks(newer, ctx.Ct)).Count == 0,
                "all that time the check run stayed in progress and the newer head was not tested");

            // Released as a person would: the target's cycles held off, the run cancelled by hand and deleted.
            await r.EditTarget(t, e => e with { Enabled = false }, ctx.Ct);
            await t.CancelRun(check.RunId!.Value, ctx.Ct);
            await Poll.True("the run to stop", TimeSpan.FromMinutes(10), async () => (await t.Run(check.RunId.Value, ctx.Ct)).Completed, ctx.Ct);
            await t.DeleteRun(check.RunId.Value, ctx.Ct);
            await r.EditTarget(t, e => e with { Enabled = true }, ctx.Ct);
            ctx.Step($"cancelled and deleted target run {check.RunId}");
            var unknown = await t.AwaitCompleted(check.Id, TimeSpan.FromMinutes(6), ctx.Ct);
            ctx.Require(unknown.Conclusion == "neutral" && unknown.Title == "Outcome unknown", "with the run deleted, the check run was neutral: outcome unknown", unknown.ToString());
            await r.AwaitAlert($"Outcome unknown on {t.Repo}", since, TimeSpan.FromMinutes(2), ctx.Ct);
            await t.AwaitCheck(newer, TimeSpan.FromMinutes(5), ctx.Ct);
            ctx.Pass("\"Outcome unknown\" was raised, and the newer head's test started");
        }
        finally
        {
            await r.SetSwitch(Replica.RefuseCancel, t, [], ctx.Ct);
            await r.EditTarget(t, e => e with { Enabled = true }, ctx.Ct);
        }
    }
}

/// <summary>A failing run that never ends, on the long-timeout workflow variant with a 2-minute target timeout.</summary>
static class NeverEnding
{
    public static async Task<(CheckRun Check, DateTimeOffset Started)> Start(ScenarioContext ctx)
    {
        var t = ctx.Target;
        await ctx.Replica.EditTarget(t, e => e with { Timeout = 2 }, ctx.Ct);
        var sha = await ResultSteps.Push(ctx, "fail Alpha, then a step that never ends", s =>
        {
            s["failing_tests"] = ResultSteps.Tests("Alpha");
            s["hang_upload_forever"] = true;
        }, t.Caller("ts-s16-long-timeout", timeoutMinutes: 2));
        var check = await ctx.Tested(sha);
        ctx.Require(Markers.Field(check.Summary, StaleRun.TimeoutMinutes) == "2", "the check run records the 2-minute timeout it was dispatched with");
        var job = await t.AwaitTestJob(check.RunId!.Value, "the tests to finish", j => j.StepNamed("main-watcher-tests-finished")?.Conclusion == "success",
            TimeSpan.FromMinutes(10), ctx.Ct);
        ctx.Require(job.StepNamed("main-watcher-test")?.Conclusion == "failure", "the tests failed and ran to completion; the job carries on");
        return (check, job.StartedAt!.Value);
    }

    /// <summary>Waits for the Planner's cancel at the run deadline: the job's start plus 2, 20 and 10 minutes.</summary>
    public static async Task<CheckRun> AtDeadline(ScenarioContext ctx, CheckRun check, DateTimeOffset started)
    {
        var deadline = started.AddMinutes(2).Add(StaleRun.JobMargin).Add(StaleRun.RunGrace);
        ctx.Step($"waiting for the run deadline, {deadline:HH:mm:ss}Z");
        var stopping = await Poll.Until("the Planner to cancel at the run deadline", deadline - DateTimeOffset.UtcNow + TimeSpan.FromMinutes(25), async () =>
            await ctx.Target.Check(check.Id, ctx.Ct) is { } c && Markers.Time(c.Summary, StaleRun.CancelRequested) is not null ? c : null, ctx.Ct, TimeSpan.FromSeconds(30));
        ctx.Require(Markers.Time(stopping.Summary, StaleRun.CancelRequested) >= deadline,
            $"the cancel was asked for at {Markers.Field(stopping.Summary, StaleRun.CancelRequested)}, not before the run deadline");
        return stopping;
    }
}
