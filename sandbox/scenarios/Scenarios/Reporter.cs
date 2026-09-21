using System.Text.Json.Nodes;
using MainWatcher.Core;
using MainWatcher.Scenarios.Infra;
using MainWatcher.Scenarios.Sandbox;

namespace MainWatcher.Scenarios.Scenarios;

/// <summary>TS-S12: cancelling a target test run marks its check run neutral, raises an alert, and retests the head.</summary>
public sealed class CancelledRun : Scenario
{
    public override string[] Covers => ["TS-S12"];
    public override string Title => "A test run cancelled by hand is neutral, raises an alert, and its head is tested again";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(14);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var since = DateTimeOffset.UtcNow;
        var sha = await ctx.Push("a 3-minute suite", s => s["slow_suite_minutes"] = 3);
        var first = await ctx.Tested(sha);
        await t.AwaitTestJob(first.RunId!.Value, "the tests to be running", j => j.StepNamed("main-watcher-test")?.Status == "in_progress",
            TimeSpan.FromMinutes(6), ctx.Ct, TimeSpan.FromSeconds(10));
        await ctx.Wait(TimeSpan.FromSeconds(40));
        await t.CancelRun(first.RunId.Value, ctx.Ct);
        ctx.Step($"cancelled target run {first.RunId} during its tests");

        var neutral = await t.AwaitCompleted(first.Id, TimeSpan.FromMinutes(8), ctx.Ct);
        ctx.Require(neutral.Conclusion == "neutral" && neutral.Title == "Infrastructure error", "the check run completed neutral: infrastructure error", neutral.ToString());
        var job = await t.TestJob(first.RunId.Value, ctx.Ct);
        ctx.Require(job?.StepNamed("main-watcher-test")?.Conclusion == "cancelled" && job.StepNamed("main-watcher-tests-finished")?.Conclusion is "skipped" or null,
            "the test step was cancelled and the finished marker never ran", job?.ToString());
        await ctx.Replica.AwaitAlert($"Infrastructure error on {t.Repo}", since, TimeSpan.FromMinutes(2), ctx.Ct);
        ctx.Pass("an \"Infrastructure error\" alert was raised");

        var retest = await t.AwaitCheck(sha, TimeSpan.FromMinutes(6), ctx.Ct, 2);
        ctx.Require(retest.StartedAt >= neutral.CompletedAt!.Value.AddMinutes(1).AddSeconds(-5),
            $"the head was tested again {(retest.StartedAt - neutral.CompletedAt.Value).TotalSeconds:0} s after the neutral result, poll_interval being 1 minute");
        var done = await t.AwaitCompleted(retest.Id, TimeSpan.FromMinutes(10), ctx.Ct);
        ctx.Require(done.Conclusion == "success" && await t.Head(ctx.Ct) == sha, "with main unchanged, the retest gave a real result: success", done.ToString());
    }
}

/// <summary>
/// TS-S13: the check run shows the suite time, the change from the last green run, the 5 slowest tests and the retry flag,
/// matching the run's CTRF reports and <c>timings.json</c>; the job summary's report job ran on the pinned reporter.
/// </summary>
public sealed class Timings : Scenario
{
    public override string[] Covers => ["TS-S13"];
    public override string Title => "The check run's timing section matches the CTRF reports, run to run and with a retry";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(12);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var first = await ctx.Push("timed tests, run 1", s => s["timed_tests"] = true);
        var one = await ctx.Result(first, "success");
        await Check(ctx, one, previous: null, retried: false);

        var second = await ctx.Push("timed tests with a flaky test, run 2", s => s["flaky_test"] = true);
        var two = await ctx.Result(second, "success");
        await Check(ctx, two, previous: (one, first), retried: true);
    }

    static async Task Check(ScenarioContext ctx, CheckRun check, (CheckRun Check, string Sha)? previous, bool retried)
    {
        var t = ctx.Target;
        var runId = check.RunId!.Value;
        var files = await t.Artifact(runId, "main-watcher-ctrf", ctx.Ct);
        var timings = JsonNode.Parse(files.Single(f => f.Key.EndsWith("timings.json", StringComparison.Ordinal)).Value)!;
        var wall = timings["tests"]!["wallClockMs"]!.GetValue<long>();
        var summary = check.Summary;
        ctx.Require(Markers.Field(summary, "suite_ms") == wall.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"check {check.Id}: suite_ms is the artifact's wall-clock time, {wall} ms");
        ctx.Require(summary.Contains($"- Suite time: {TimingSection.Duration(wall)} wall clock", StringComparison.Ordinal),
            $"check {check.Id}: the suite time reads {TimingSection.Duration(wall)}");

        var tests = files.Where(f => f.Key.EndsWith(".ctrf.json", StringComparison.Ordinal))
            .SelectMany(f => JsonNode.Parse(f.Value)!["results"]!["tests"]!.AsArray())
            .Select(n => (Name: n!["name"]!.GetValue<string>(), Duration: n["duration"]!.GetValue<long>()))
            .OrderByDescending(x => x.Duration).Take(5).ToList();
        foreach (var (name, duration) in tests)
            ctx.Require(summary.Contains($"| {name}", StringComparison.Ordinal) && summary.Contains(TimingSection.Duration(duration), StringComparison.Ordinal),
                $"check {check.Id}: the slowest tests include {name}, {TimingSection.Duration(duration)}");

        ctx.Require(summary.Contains(retried ? "- Retried: yes" : "- Retried: no", StringComparison.Ordinal) && timings["retried"]!.GetValue<bool>() == retried,
            $"check {check.Id}: the retry flag says {(retried ? "yes" : "no")}, as timings.json does");
        if (previous is { } before)
            ctx.Require(summary.Contains("- Change from the last green run:", StringComparison.Ordinal)
                && summary.Contains(Target.Short(before.Sha), StringComparison.Ordinal),
                $"check {check.Id}: the change is measured against the last green run, {Target.Short(before.Sha)}");

        var report = (await t.Jobs(runId, ctx.Ct))?.SingleOrDefault(j => j.Name.EndsWith("/ report", StringComparison.Ordinal));
        ctx.Require(report?.Conclusion == "success", $"target run {runId}: the report job, which writes the job summary, succeeded", report?.ToString());
        ctx.Require((await t.Artifact(runId, "main-watcher-report", ctx.Ct)).Count > 0, $"target run {runId}: the reporter saved its history artifact");
    }
}

/// <summary>
/// TS-S14 (a), (b) and (d): a Reporter stopped after one of its lock writes, before completing the check run, is replayed on
/// the next cycle with no duplicate issue or comment; and a human close before the replay is an override, with no new lock.
/// </summary>
public sealed class ReporterReplay : Scenario
{
    public override string[] Covers => ["TS-S14 (a)", "TS-S14 (b)", "TS-S14 (d)"];
    public override string Title => "A Reporter stopped after a lock write is replayed without duplicates, and respects a close by hand";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(28);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var r = ctx.Replica;

        // (a) Stopped after creating the lock.
        await r.SetSwitch(Replica.ExitAfter, t, ["create"], ctx.Ct);
        var since = DateTimeOffset.UtcNow;
        var sha = await ctx.PushFailing("Alpha");
        var check = await ctx.Tested(sha);
        var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(10), ctx.Ct);
        await Stopped(ctx, since, "create");
        var done = await t.AwaitCompleted(check.Id, TimeSpan.FromMinutes(5), ctx.Ct);
        ctx.Require(done.Conclusion == "failure" && done.Title == "Tests failed", $"(a) the replay completed check {check.Id} as failure, never stale", done.ToString());
        lockIssue = await t.Issue(lockIssue.Number, ctx.Ct);
        ctx.Require((await t.Locks(ctx.Ct)).Count(l => l.CreatedAt >= since.AddSeconds(-5)) == 1
            && (await t.Comments(lockIssue.Number, ctx.Ct)).Count == 0 && Markers.Field(lockIssue.Body, "reported_check") == check.Id.ToString(),
            $"(a) one lock, #{lockIssue.Number}, no comment, and its marker records check {check.Id}");

        // (b) Stopped after the comment, then after the update.
        await r.SetSwitch(Replica.ExitAfter, t, ["comment", "update"], ctx.Ct);
        since = DateTimeOffset.UtcNow;
        sha = await ctx.PushFailing("Beta");
        check = await ctx.Tested(sha);
        done = await t.AwaitCompleted(check.Id, TimeSpan.FromMinutes(14), ctx.Ct);
        ctx.Require(done.Conclusion == "failure", $"(b) check {check.Id} completed as failure after the replays", done.ToString());
        var stops = (await r.Cycles(t, since, ctx.Ct)).Count(c => c.Conclusion == "failure");
        ctx.Require(stops >= 2, $"(b) the cycle was stopped after the comment and after the update ({stops} stopped cycles)");
        var comments = await t.Comments(lockIssue.Number, ctx.Ct);
        lockIssue = await t.Issue(lockIssue.Number, ctx.Ct);
        ctx.Require(comments.Count == 1 && comments[0].Body.Contains($"check={check.Id}", StringComparison.Ordinal)
            && Markers.Field(lockIssue.Body, "reported_check") == check.Id.ToString(),
            $"(b) exactly one comment for check {check.Id}, and the marker records it");

        // (d) Stopped after creating a new lock, which a person closes before the replay.
        await t.CloseIssue(lockIssue.Number, ctx.Ct);
        await Poll.True($"the override comment on #{lockIssue.Number}", TimeSpan.FromMinutes(5), async () =>
            (await t.Comments(lockIssue.Number, ctx.Ct)).Any(c => c.Body.Contains("closed=override", StringComparison.Ordinal)), ctx.Ct);
        await r.SetSwitch(Replica.ExitAfter, t, ["create"], ctx.Ct);
        since = DateTimeOffset.UtcNow;
        sha = await ctx.PushFailing("Gamma");
        check = await ctx.Tested(sha);
        var created = await Poll.Until("the new lock", TimeSpan.FromMinutes(10),
            async () => (await t.Locks(ctx.Ct)).FirstOrDefault(l => l.CreatedAt >= since.AddSeconds(-5)), ctx.Ct, TimeSpan.FromSeconds(3));
        await t.CloseIssue(created.Number, ctx.Ct);
        ctx.Require((await t.Check(check.Id, ctx.Ct)).Status == "in_progress",
            $"(d) lock #{created.Number} was closed by hand while check {check.Id} was still waiting for its replay");
        await r.SetSwitch(Replica.ExitAfter, t, [], ctx.Ct);
        done = await t.AwaitCompleted(check.Id, TimeSpan.FromMinutes(5), ctx.Ct);
        ctx.Require(done.Conclusion == "failure" && done.Summary.Contains(
            $"This result was already written to lock issue {created.Url}, which has been closed since, so no lock was opened.", StringComparison.Ordinal),
            $"(d) check {check.Id} completed as failure, saying its lock was closed since", done.ToString());
        await Poll.True($"the override comment on #{created.Number}", TimeSpan.FromMinutes(5), async () =>
            (await t.Comments(created.Number, ctx.Ct)).Any(c => c.Body.Contains("closed this lock by hand", StringComparison.Ordinal)), ctx.Ct);
        ctx.Pass($"(d) the override comment was posted on #{created.Number}");
        ctx.Require((await t.Locks(ctx.Ct)).Count(l => l.CreatedAt >= since.AddSeconds(-5)) == 1, "(d) no new lock was created");
    }

    static async Task Stopped(ScenarioContext ctx, DateTimeOffset since, string write)
    {
        var cycle = await Poll.Until($"a cycle stopped after the {write} write", TimeSpan.FromMinutes(5), async () =>
            (await ctx.Replica.Cycles(ctx.Target, since, ctx.Ct)).FirstOrDefault(c => c.Conclusion == "failure"), ctx.Ct);
        ctx.Require((await ctx.Replica.CycleLog(cycle.Id, ctx.Target, ctx.Ct)).Contains($"exiting after the {write} write", StringComparison.Ordinal),
            $"cycle {cycle.Id} stopped right after the {write} write");
    }
}

/// <summary>TS-S14 (c): with issue writes failing for 20 min, a "reporting pending" alert is raised, and the lock appears once writes succeed.</summary>
public sealed class ReportingPending : Scenario
{
    public override string[] Covers => ["TS-S14 (c)"];
    public override string Title => "Issue writes refused for 20 minutes raise \"reporting pending\", and the lock appears once they work";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(26);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        await ctx.Replica.SetReadOnlyIssues(t, true, ctx.Ct);
        var since = DateTimeOffset.UtcNow;
        try
        {
            var sha = await ctx.PushFailing("Alpha");
            var check = await ctx.Tested(sha);
            var job = await t.AwaitTestJob(check.RunId!.Value, "the test job to complete", j => j.Completed, TimeSpan.FromMinutes(10), ctx.Ct);
            ctx.Step($"the test job completed at {job.CompletedAt:HH:mm:ss}Z; the watcher's token can only read issues");
            var alert = await ctx.Replica.AwaitAlert($"Reporting pending on {t.Repo}", since, TimeSpan.FromMinutes(22), ctx.Ct);
            ctx.Require(alert.Author == "mw-doorbell[bot]" && alert.CreatedAt >= job.CompletedAt!.Value.AddMinutes(15),
                $"the worker raised \"Reporting pending\" (#{alert.Number}) {(alert.CreatedAt - job.CompletedAt!.Value).TotalMinutes:0} min after the job completed");
            ctx.Require((await t.Check(check.Id, ctx.Ct)).Status == "in_progress" && (await t.Locks(ctx.Ct, "open")).Count == 0,
                "meanwhile the check run stayed in progress and no lock existed");
            var cycles = (await ctx.Replica.Cycles(t, job.CompletedAt!.Value, ctx.Ct)).Count(c => c.Conclusion == "failure");
            ctx.Require(cycles >= 5, $"the worker kept asking: {cycles} cycles failed on the refused writes");
            await Poll.Till(since.AddMinutes(20), ctx.Ct);
            await ctx.Replica.SetReadOnlyIssues(t, false, ctx.Ct);
            ctx.Step("gave the watcher back its issue writes");
            var done = await t.AwaitCompleted(check.Id, TimeSpan.FromMinutes(5), ctx.Ct);
            var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(2), ctx.Ct);
            ctx.Require(done.Conclusion == "failure" && Markers.Field(lockIssue.Body, "reported_check") == check.Id.ToString(),
                $"once writes worked, lock #{lockIssue.Number} appeared and check {check.Id} completed as failure");
            ctx.Require((await t.Checks(sha, ctx.Ct)).Count == 1 && (await t.Locks(ctx.Ct)).Count(l => l.CreatedAt >= since) == 1,
                "no duplicate lock, and the head was never retested while its report was owed");
        }
        finally { await ctx.Replica.SetReadOnlyIssues(t, false, ctx.Ct); }
    }
}

/// <summary>
/// TS-S18: a neutral head is retested, even when the cycle is killed right after writing the neutral; after three neutral
/// results a "head untestable" alert is raised and no fourth test starts until a forced dispatch; a new head gives a real result.
/// </summary>
public sealed class NeutralRetries : Scenario
{
    public override string[] Covers => ["TS-S18"];
    public override string Title => "Neutral heads are retested up to three times, then flagged untestable until forced";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(32);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var r = ctx.Replica;
        var since = DateTimeOffset.UtcNow;
        await r.SetSwitch(Replica.ExitAfter, t, ["check:neutral"], ctx.Ct);
        string sha;
        CheckRun first;
        try
        {
            sha = await ctx.Push("restore fails", s => s["fail_restore"] = true);
            first = await t.AwaitCompleted((await ctx.Tested(sha)).Id, TimeSpan.FromMinutes(10), ctx.Ct);
            var killed = await Poll.Until("the cycle stopped after the neutral write", TimeSpan.FromMinutes(3), async () =>
                (await r.Cycles(t, since, ctx.Ct)).FirstOrDefault(c => c.Conclusion == "failure"), ctx.Ct, TimeSpan.FromSeconds(10));
            var log = await r.CycleLog(killed.Id, t, ctx.Ct);
            ctx.Require(first.Conclusion == "neutral" && log.Contains("exiting after the check:neutral write", StringComparison.Ordinal)
                && !log.Contains("No eligible head.", StringComparison.Ordinal),
                $"cycle {killed.Id} was killed right after completing check {first.Id} as neutral, before planning");
        }
        finally { await r.SetSwitch(Replica.ExitAfter, t, [], ctx.Ct); }

        var second = await t.AwaitCompleted((await t.AwaitCheck(sha, TimeSpan.FromMinutes(6), ctx.Ct, 2)).Id, TimeSpan.FromMinutes(10), ctx.Ct);
        ctx.Require(second.Conclusion == "neutral", "the head was still retested after the killed cycle, and was neutral again");
        await r.AwaitAlert($"Infrastructure errors twice in a row on {t.Repo}", since, TimeSpan.FromMinutes(2), ctx.Ct);
        ctx.Pass("\"Infrastructure errors twice in a row\" was raised");
        var third = await t.AwaitCompleted((await t.AwaitCheck(sha, TimeSpan.FromMinutes(6), ctx.Ct, 3)).Id, TimeSpan.FromMinutes(10), ctx.Ct);
        ctx.Require(third.Conclusion == "neutral", "a third test was neutral too");
        var alert = await r.AwaitAlert($"Head untestable on {t.Repo}", since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Require(alert.Body.Contains("which has 3 `neutral` check runs", StringComparison.Ordinal) && alert.Body.Contains($"untestable sha={sha}", StringComparison.Ordinal),
            $"\"Head untestable\" (#{alert.Number}) was raised for {Target.Short(sha)}");

        await ctx.Wait(TimeSpan.FromMinutes(4));
        ctx.Require((await t.Checks(sha, ctx.Ct)).Count == 3, "four minutes on, no fourth test had started");
        var plain = await r.DispatchCycle(t, ctx.Ct);
        await r.AwaitRun(plain, TimeSpan.FromMinutes(8), ctx.Ct);
        ctx.Require((await r.CycleLog(plain, t, ctx.Ct)).Contains("No eligible head.", StringComparison.Ordinal) && (await t.Checks(sha, ctx.Ct)).Count == 3,
            $"a dispatched cycle without force (run {plain}) started nothing");
        var forced = await r.DispatchCycle(t, ctx.Ct, force: true);
        await r.AwaitRun(forced, TimeSpan.FromMinutes(8), ctx.Ct);
        var fourth = await t.AwaitCheck(sha, TimeSpan.FromMinutes(3), ctx.Ct, 4);
        ctx.Pass($"a forced dispatch (run {forced}) started a fourth test, check {fourth.Id}");
        await t.AwaitCompleted(fourth.Id, TimeSpan.FromMinutes(10), ctx.Ct);

        await ctx.Result(await ctx.Push("restore works again", s => s["fail_restore"] = false), "success");
        ctx.Pass("with the feed back, the next head gave a real result: success");
    }
}
