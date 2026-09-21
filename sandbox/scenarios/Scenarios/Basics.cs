using MainWatcher.Core;
using MainWatcher.Scenarios.Sandbox;

namespace MainWatcher.Scenarios.Scenarios;

/// <summary>TS-S1: an unchanged head causes no <c>watch.yml</c> run and no test run.</summary>
public sealed class IdleHead : Scenario
{
    public override string[] Covers => ["TS-S1"];
    public override string Title => "An unchanged head causes no watch.yml run and no test run";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(11);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var head = await t.Head(ctx.Ct);
        var checks = (await t.Checks(head, ctx.Ct)).Count;
        var since = DateTimeOffset.UtcNow;
        ctx.Step($"leaving {Target.Short(head)} alone for 10 minutes");
        await ctx.Wait(TimeSpan.FromMinutes(10));
        ctx.Require(await t.Head(ctx.Ct) == head, "main did not move");
        var cycles = await ctx.Replica.Cycles(t, since, ctx.Ct);
        ctx.Require(cycles.Count == 0, "no watch.yml cycle was dispatched for the target in 10 minutes", string.Join("; ", cycles));
        var runs = await t.Runs("main-watcher-tests.yml", since, ctx.Ct);
        ctx.Require(runs.Count == 0, "no target test run started", string.Join("; ", runs));
        ctx.Require((await t.Checks(head, ctx.Ct)).Count == checks, $"the head still has {checks} main-watcher check run(s)");
    }
}

/// <summary>TS-S2: three quick pushes during a running test lead to exactly one further test, of the newest commit.</summary>
public sealed class QuickPushes : Scenario
{
    public override string[] Covers => ["TS-S2"];
    public override string Title => "Three quick pushes during a running test: one further test, of the newest; the lock lists every push";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(18);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var green = await t.Head(ctx.Ct);
        var since = DateTimeOffset.UtcNow;
        var first = await ctx.Push("fail Alpha, 4-minute suite", s =>
        {
            s["failing_tests"] = new System.Text.Json.Nodes.JsonArray("Alpha");
            s["slow_suite_minutes"] = 4;
            s["timed_tests"] = false;
        });
        var check1 = await ctx.Tested(first);
        await t.AwaitTestJob(check1.RunId!.Value, "the tests to be running", j => j.StepNamed("main-watcher-test")?.Status == "in_progress",
            TimeSpan.FromMinutes(6), ctx.Ct, TimeSpan.FromSeconds(10));
        var second = await ctx.Push("fail Alpha and Beta", s => s["failing_tests"] = new System.Text.Json.Nodes.JsonArray("Alpha", "Beta"));
        var third = await ctx.Push("fail Beta", s => s["failing_tests"] = new System.Text.Json.Nodes.JsonArray("Beta"));
        ctx.Require((await t.Check(check1.Id, ctx.Ct)).Status == "in_progress", "the second and third pushes landed while the first test ran");

        var done1 = await t.AwaitCompleted(check1.Id, TimeSpan.FromMinutes(12), ctx.Ct);
        ctx.Require(done1.Conclusion == "failure", $"the first push's check run failed ({done1})");
        var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(4), ctx.Ct);
        ctx.Require(lockIssue.Body.Contains($"Since the last green commit", StringComparison.Ordinal)
            && lockIssue.Body.Contains(Target.Short(green), StringComparison.Ordinal), $"lock #{lockIssue.Number} counts from the last green commit {Target.Short(green)}");
        foreach (var (sha, n) in new[] { (first, 1), (second, 2), (third, 3) })
            ctx.Require(lockIssue.Body.Contains(Target.Short(sha), StringComparison.Ordinal), $"lock #{lockIssue.Number} lists push {n}, {Target.Short(sha)}");
        ctx.Require(Markers.Field(lockIssue.Body, "first_red") == first, "its marker names the first push as first_red");

        var check3 = await ctx.Tested(third);
        var done3 = await t.AwaitCompleted(check3.Id, TimeSpan.FromMinutes(14), ctx.Ct);
        ctx.Require(done3.Conclusion == "failure", $"the newest push was tested, and failed ({done3})");
        ctx.Require((await t.Checks(second, ctx.Ct)).Count == 0, $"the middle push {Target.Short(second)} was never tested");
        var comments = await Poll(ctx, lockIssue.Number, check3.Id);
        ctx.Require(comments.Count(c => c.Author == SandboxOrg.BotLogin) == 1 && comments[^1].Body.Contains("Beta", StringComparison.Ordinal),
            "the second failure added exactly one comment to the lock, naming Beta");
        ctx.Require((await t.Locks(ctx.Ct, "open")).Count == 1, "no second lock was opened");
    }

    static async Task<List<Comment>> Poll(ScenarioContext ctx, int issue, long check) =>
        await Infra.Poll.Until($"the lock's comment for check {check}", TimeSpan.FromMinutes(3), async () =>
        {
            var comments = await ctx.Target.Comments(issue, ctx.Ct);
            return comments.Any(c => c.Body.Contains($"check={check}", StringComparison.Ordinal)) ? comments : null;
        }, ctx.Ct);
}

/// <summary>TS-S3: a green run closes the lock; an override lifts the gate; a failure on a newer commit opens a new issue.</summary>
public sealed class Resolution : Scenario
{
    public override string[] Covers => ["TS-S3"];
    public override string Title => "A green run closes the lock; an override lifts the gate; a later failure opens a new lock";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(24);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var since = DateTimeOffset.UtcNow;
        await ctx.Result(await ctx.PushFailing("Alpha"), "failure");
        var first = await t.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Pass($"a failure opened lock #{first.Number}");

        var green = await ctx.PushGreen();
        var passed = await ctx.Result(green, "success");
        var closed = await Infra.Poll.Until($"lock #{first.Number} to close", TimeSpan.FromMinutes(3), async () =>
            await t.Issue(first.Number, ctx.Ct) is { State: "closed" } issue ? issue : null, ctx.Ct);
        ctx.Require(closed.ClosedBy == SandboxOrg.BotLogin && closed.StateReason == "completed", $"the green run's App closed lock #{first.Number} as completed",
            $"closed by {closed.ClosedBy}, {closed.StateReason}");
        ctx.Require((await t.Comments(first.Number, ctx.Ct)).Any(c => c.Body.Contains($"check={passed.Id} sha={green} closed=green", StringComparison.Ordinal)),
            "with a comment naming the green check run");

        since = DateTimeOffset.UtcNow;
        await ctx.Result(await ctx.PushFailing("Beta"), "failure");
        var second = await t.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        await t.CloseIssue(second.Number, ctx.Ct);
        ctx.Step($"closed lock #{second.Number} by hand");
        await Infra.Poll.True($"the override comment on #{second.Number}", TimeSpan.FromMinutes(5), async () =>
            (await t.Comments(second.Number, ctx.Ct)).Any(c => c.Author == SandboxOrg.BotLogin
                && c.Body.Contains("closed this lock by hand", StringComparison.Ordinal) && c.Body.Contains("closed=override", StringComparison.Ordinal)), ctx.Ct);
        ctx.Pass($"closing lock #{second.Number} by hand was recorded as an override");

        var pr = await t.OpenPullRequest("ts-s3-override", ctx.Ct);
        since = DateTimeOffset.UtcNow;
        await t.Enqueue(pr, ctx.Ct);
        await t.AwaitMerged(pr, TimeSpan.FromMinutes(8), ctx.Ct);
        ctx.Pass($"with the lock overridden, unlabelled #{pr.Number} merged");

        var merged = await t.Head(ctx.Ct);
        await ctx.Result(merged, "failure");
        var third = await t.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Require(third.Number != second.Number && third.Body.Contains($"The previous lock, {second.Url}, was closed by hand.", StringComparison.Ordinal),
            $"the failure on the newer commit {Target.Short(merged)} opened a new lock, #{third.Number}, pointing back at the overridden one");
    }
}

/// <summary>TS-S6: a hand-made <c>main-broken</c> issue does not lock.</summary>
public sealed class HandMadeIssue : Scenario
{
    public override string[] Covers => ["TS-S6"];
    public override string Title => "A main-broken issue opened by a person does not lock";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(6);
    public override bool NeedsWorker => false;

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var lease = Markers.Stamp(DateTimeOffset.UtcNow.AddHours(4));
        var issue = (await t.GitHub.Post($"repos/{t.Repo}/issues", new
        {
            title = "main is broken (opened by a person)",
            body = $"Opened by the scenario suite as {ctx.Sandbox.Me}, not by the App (TS-S6).\n\n<!-- main-watcher lease_until={lease} -->",
            labels = new[] { SandboxOrg.LockLabel }
        }, ctx.Ct))!["number"]!.GetValue<int>();
        ctx.Step($"opened #{issue} with the main-broken label and a lease");
        try
        {
            var pr = await t.OpenPullRequest("ts-s6", ctx.Ct);
            var since = DateTimeOffset.UtcNow;
            await t.Enqueue(pr, ctx.Ct);
            await t.AwaitMerged(pr, TimeSpan.FromMinutes(8), ctx.Ct);
            ctx.Pass($"unlabelled #{pr.Number} merged");
            var gate = (await t.GateRuns(pr.Number, since, ctx.Ct)).First();
            var (_, log) = await t.GateJob(gate.Id, ctx.Ct);
            ctx.Require(GateLog.Has(log, $"No open `main-broken` issue authored by `{SandboxOrg.BotLogin}`"),
                "the gate found no lock: the issue was not written by the App");
        }
        finally { await t.CloseIssue(issue, ctx.Ct, "Closed by the scenario suite (TS-S6)."); }
    }
}
