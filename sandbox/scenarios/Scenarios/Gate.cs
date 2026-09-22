using MainWatcher.Scenarios.Infra;
using MainWatcher.Scenarios.Sandbox;

namespace MainWatcher.Scenarios.Scenarios;

/// <summary>Reading gate verdicts, which the gate writes to its job's log.</summary>
public static class GateLog
{
    /// <summary>Whether <paramref name="log"/> contains <paramref name="text"/>, ignoring the Markdown backticks GitHub may drop.</summary>
    public static bool Has(string log, string text) => log.Replace("`", "").Contains(text.Replace("`", ""), StringComparison.Ordinal);

    /// <summary>The newest completed gate run for a pull request's merge group, with its gate job's log.</summary>
    public static async Task<(Run Run, string Log)> Verdict(ScenarioContext ctx, PullRequest pr, DateTimeOffset since, int attempt = 0)
    {
        var run = await Poll.Until($"a completed gate run for #{pr.Number}", TimeSpan.FromMinutes(8), async () =>
            (await ctx.Target.GateRuns(pr.Number, since, ctx.Ct)).FirstOrDefault(r => r.Completed && r.Attempt >= attempt), ctx.Ct);
        var (_, log) = await ctx.Target.GateJob(run.Id, ctx.Ct, run.Attempt);
        return (run, log);
    }

    /// <summary>Whether the gate run also ran its fail-open job, which records a gate that passed without enforcing a lock.</summary>
    public static async Task<bool> FailedOpen(ScenarioContext ctx, Run run) =>
        (await ctx.Target.Jobs(run.Id, ctx.Ct, run.Attempt) ?? []).Any(j => j.Name == "main-watcher/gate-fail-open" && j.Conclusion == "success");
}

/// <summary>
/// TS-S4: while locked, an unlabelled PR is removed from the queue and a <c>fixes-main</c> PR merges. TS-S5: a batched group
/// mixing a fix and a non-fix PR fails the gate. The lock is a real one, so the merges keep <c>main</c> red and it stays open.
/// </summary>
public sealed class LockedQueue : Scenario
{
    public override string[] Covers => ["TS-S4", "TS-S5"];
    public override string Title => "While locked, the queue takes only fixes-main PRs, one at a time or batched";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(20);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var since = DateTimeOffset.UtcNow;
        await ctx.Result(await ctx.PushFailing("Alpha"), "failure");
        var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Pass($"main is locked by #{lockIssue.Number}");

        // TS-S4
        var plain = await t.OpenPullRequest("ts-s4-plain", ctx.Ct);
        var queued = DateTimeOffset.UtcNow;
        await t.Enqueue(plain, ctx.Ct);
        await t.AwaitRemovedFromQueue(plain, TimeSpan.FromMinutes(8), ctx.Ct);
        var (run, log) = await GateLog.Verdict(ctx, plain, queued);
        ctx.Require(run.Conclusion == "failure" && GateLog.Has(log, $"`main` is locked by #{lockIssue.Number}")
            && GateLog.Has(log, $"Not labelled: #{plain.Number}"), $"TS-S4: the gate failed unlabelled #{plain.Number}'s group, and the queue removed it", run.ToString());

        var fix = await t.OpenPullRequest("ts-s4-fix", ctx.Ct, [SandboxOrg.FixLabel]);
        queued = DateTimeOffset.UtcNow;
        await t.Enqueue(fix, ctx.Ct);
        await t.AwaitMerged(fix, TimeSpan.FromMinutes(8), ctx.Ct);
        (run, log) = await GateLog.Verdict(ctx, fix, queued);
        ctx.Require(run.Conclusion == "success" && GateLog.Has(log, "every PR in this merge group is labelled `fixes-main`"),
            $"TS-S4: fixes-main #{fix.Number} passed the gate and merged");

        // TS-S5: queued back to back, the second group is built on the first, so it holds both.
        var fix2 = await t.OpenPullRequest("ts-s5-fix", ctx.Ct, [SandboxOrg.FixLabel]);
        var plain2 = await t.OpenPullRequest("ts-s5-plain", ctx.Ct);
        queued = DateTimeOffset.UtcNow;
        await t.Enqueue(fix2, ctx.Ct);
        await t.Enqueue(plain2, ctx.Ct);
        await t.AwaitMerged(fix2, TimeSpan.FromMinutes(10), ctx.Ct);
        await t.AwaitRemovedFromQueue(plain2, TimeSpan.FromMinutes(10), ctx.Ct);
        var fixRun = (await t.GateRuns(fix2.Number, queued, ctx.Ct)).Last();
        (run, log) = await GateLog.Verdict(ctx, plain2, queued);
        var baseSha = run.HeadBranch[(run.HeadBranch.LastIndexOf('-') + 1)..];
        ctx.Require(baseSha == fixRun.HeadSha, $"TS-S5: #{plain2.Number}'s group was built on #{fix2.Number}'s, so it held both",
            $"its base {Target.Short(baseSha)}, #{fix2.Number}'s group head {Target.Short(fixRun.HeadSha)}");
        ctx.Require(run.Conclusion == "failure" && GateLog.Has(log, $"Not labelled: #{plain2.Number}"),
            $"TS-S5: the gate failed the mixed group for #{plain2.Number}, and #{fix2.Number} merged on its own");
    }
}

/// <summary>TS-S9: when the gate cannot reach the API, it fails open with a warning, and the next watcher run reports the merge.</summary>
public sealed class GateApiDown : Scenario
{
    public override string[] Covers => ["TS-S9"];
    public override string Title => "A gate that cannot read the API fails open, and the watcher reports the unlabelled merge";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(16);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var since = DateTimeOffset.UtcNow;
        await ctx.Result(await ctx.PushFailing("Alpha"), "failure");
        var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        await t.Commit(new Dictionary<string, string?> { [Target.GateFile] = ctx.Sandbox.Templates.Gate("ts-s9-invalid-token") },
            "Gate: an invalid token (TS-S9)", ctx.Ct);
        ctx.Step("gave the gate an invalid token");

        var pr = await t.OpenPullRequest("ts-s9", ctx.Ct);
        var queued = DateTimeOffset.UtcNow;
        await t.Enqueue(pr, ctx.Ct);
        await t.AwaitMerged(pr, TimeSpan.FromMinutes(8), ctx.Ct);
        var (run, log) = await GateLog.Verdict(ctx, pr, queued);
        ctx.Require(run.Conclusion == "success" && GateLog.Has(log, "The gate could not read the lock state") && log.Contains("HTTP 401", StringComparison.Ordinal),
            $"unlabelled #{pr.Number} merged through a gate that failed open on a 401, with a warning");
        ctx.Require(await GateLog.FailedOpen(ctx, run), "the gate recorded its fail-open (main-watcher/gate-fail-open)");

        var key = $"merged_while_locked pr={pr.Number}";
        await Poll.True($"lock #{lockIssue.Number} to report #{pr.Number}", TimeSpan.FromMinutes(8), async () =>
            (await t.Issue(lockIssue.Number, ctx.Ct)).Body.Contains(key, StringComparison.Ordinal), ctx.Ct);
        ctx.Pass($"the next watcher run reported #{pr.Number} on lock #{lockIssue.Number}");
        var alert = await ctx.Replica.AwaitAlert($"Merged while locked on {t.Repo}", queued, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Require((await ctx.Replica.AlertText(alert, ctx.Ct)).Contains(key, StringComparison.Ordinal), $"and raised alert #{alert.Number} naming it");
    }
}
