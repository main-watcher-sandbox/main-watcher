using MainWatcher.Core;
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
/// TS-S4: while locked, an unlabelled PR is removed from the queue and a <c>fixes-main</c> PR merges. The lock is a real one,
/// so the merge keeps <c>main</c> red and it stays open.
/// </summary>
public sealed class LockedQueue : Scenario
{
    public override string[] Covers => ["TS-S4"];
    public override string Title => "While locked, the queue drops an unlabelled PR and merges a fixes-main one";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(14);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var since = DateTimeOffset.UtcNow;
        await ctx.Result(await ctx.PushFailing("Alpha"), "failure");
        var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Pass($"main is locked by #{lockIssue.Number}");

        var plain = await t.OpenPullRequest("ts-s4-plain", ctx.Ct);
        var queued = DateTimeOffset.UtcNow;
        await t.Enqueue(plain, ctx.Ct);
        await t.AwaitRemovedFromQueue(plain, TimeSpan.FromMinutes(8), ctx.Ct);
        var (run, log) = await GateLog.Verdict(ctx, plain, queued);
        ctx.Require(run.Conclusion == "failure" && GateLog.Has(log, $"`main` is locked by #{lockIssue.Number}")
            && GateLog.Has(log, $"Not labelled: #{plain.Number}"), $"the gate failed unlabelled #{plain.Number}'s group, and the queue removed it", run.ToString());

        var fix = await t.OpenPullRequest("ts-s4-fix", ctx.Ct, [SandboxOrg.FixLabel]);
        queued = DateTimeOffset.UtcNow;
        await t.Enqueue(fix, ctx.Ct);
        await t.AwaitMerged(fix, TimeSpan.FromMinutes(8), ctx.Ct);
        (run, log) = await GateLog.Verdict(ctx, fix, queued);
        ctx.Require(run.Conclusion == "success" && GateLog.Has(log, "every PR in this merge group is labelled `fixes-main`"),
            $"fixes-main #{fix.Number} passed the gate and merged");
    }
}

/// <summary>
/// TS-S5: a batched group mixing a fix and a non-fix PR fails the gate. It runs as the onboarding guide runs it
/// (docs/onboarding.md): with the target disabled, so no cycle closes the lock or reconciles against it, <c>lock.yml</c> opens
/// an App-authored lock with a short lease and closes it as the App afterwards. So every release run also dispatches
/// <c>lock.yml</c>, which holds the main App key.
/// </summary>
public sealed class BatchedGroup : Scenario
{
    public override string[] Covers => ["TS-S5"];
    public override string Title => "Under a lock.yml lock, a batched group mixing a fixes-main PR and an unlabelled one fails the gate";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(14);

    // No cycle runs for a disabled target, so nothing here waits on the worker.
    public override bool NeedsWorker => false;

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        await ctx.Replica.EditTarget(t, e => e with { Enabled = false }, ctx.Ct);
        ctx.Step("disabled the target's entry, as onboarding does before its TS-S5");
        var lockIssue = await ctx.Replica.OpenHandMadeLock(t, leaseHours: 1, ctx.Ct);
        try
        {
            ctx.Require(lockIssue.Author == SandboxOrg.BotLogin && lockIssue.Body.Contains(Lease.Until, StringComparison.Ordinal),
                $"lock.yml opened App-authored lock #{lockIssue.Number} with a lease");

            // Queued back to back, the second group is built on the first, so it holds both.
            var fix = await t.OpenPullRequest("ts-s5-fix", ctx.Ct, [SandboxOrg.FixLabel]);
            var plain = await t.OpenPullRequest("ts-s5-plain", ctx.Ct);
            var queued = DateTimeOffset.UtcNow;
            await t.Enqueue(fix, ctx.Ct);
            await t.Enqueue(plain, ctx.Ct);
            await t.AwaitMerged(fix, TimeSpan.FromMinutes(10), ctx.Ct);
            await t.AwaitRemovedFromQueue(plain, TimeSpan.FromMinutes(10), ctx.Ct);
            var fixRun = (await t.GateRuns(fix.Number, queued, ctx.Ct)).Last();
            var (run, log) = await GateLog.Verdict(ctx, plain, queued);
            var baseSha = run.HeadBranch[(run.HeadBranch.LastIndexOf('-') + 1)..];
            ctx.Require(baseSha == fixRun.HeadSha, $"#{plain.Number}'s group was built on #{fix.Number}'s, so it held both",
                $"its base {Target.Short(baseSha)}, #{fix.Number}'s group head {Target.Short(fixRun.HeadSha)}");
            ctx.Require(run.Conclusion == "failure" && GateLog.Has(log, $"`main` is locked by #{lockIssue.Number}")
                && GateLog.Has(log, $"Not labelled: #{plain.Number}"),
                $"the gate failed the mixed group for #{plain.Number}, and #{fix.Number} merged on its own", run.ToString());
        }
        finally { await ctx.Replica.CloseHandMadeLocks(t, ctx.Ct); }

        var closed = await t.Issue(lockIssue.Number, ctx.Ct);
        ctx.Require(closed.State == "closed" && closed.ClosedBy == SandboxOrg.BotLogin,
            $"lock.yml closed #{lockIssue.Number} as the App, so no override is recorded", $"{closed.State}, closed by {closed.ClosedBy}");
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
