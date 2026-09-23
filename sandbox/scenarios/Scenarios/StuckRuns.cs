using MainWatcher.Scenarios.Infra;
using MainWatcher.Scenarios.Sandbox;

namespace MainWatcher.Scenarios.Scenarios;

/// <summary>
/// TS-S19 (ADR-020): a <c>watch.yml</c> run held at an environment gate with a required reviewer is left alone, and 20 minutes
/// after it was created an alert names the reviewer; once the reviewer is gone and the run cleared, the target's next cycle
/// runs and reports. Only this target's cycles use the reviewed environment, so the rest of the pool carries on meanwhile.
/// </summary>
public sealed class ReviewerHeldCycle : Scenario
{
    public override string[] Covers => ["TS-S19"];
    public override string Title => "A watch.yml run waiting for a reviewer is left alone and named in an alert";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(30);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var r = ctx.Replica;
        var reviewer = ctx.Sandbox.Me;
        await r.RequireReviewer(reviewer, ctx.Ct);
        await r.SetReviewed(t, true, ctx.Ct);
        var since = DateTimeOffset.UtcNow;
        Run? held = null;
        try
        {
            var sha = await ctx.PushGreen("a head whose cycle will wait for a reviewer");
            held = await Poll.Until("the worker's cycle to wait at the reviewed environment", TimeSpan.FromMinutes(6), async () =>
                (await r.Cycles(t, since, ctx.Ct)).FirstOrDefault(c => c.Status == "waiting"), ctx.Ct, TimeSpan.FromSeconds(15));
            var reviewers = await r.GateReviewers(held.Id, ctx.Ct);
            ctx.Require(reviewers.Contains(reviewer, StringComparer.OrdinalIgnoreCase),
                $"cycle {held.Id} is waiting at {Replica.ReviewedEnvironment}, whose gate lists {reviewer}", string.Join(", ", reviewers));

            var alert = await r.AwaitAlert($"`watch.yml` run waiting for a reviewer on {t.Repo}", since,
                held.CreatedAt.AddMinutes(24) - DateTimeOffset.UtcNow, ctx.Ct);
            var text = await r.AlertText(alert, ctx.Ct);
            ctx.Require(alert.Author == "mw-doorbell[bot]" && alert.CreatedAt >= held.CreatedAt.AddMinutes(20)
                && text.Contains($"`{reviewer}`", StringComparison.Ordinal) && text.Contains("must have no required reviewers", StringComparison.Ordinal),
                $"the worker raised \"waiting for a reviewer\" (#{alert.Number}) {(alert.CreatedAt - held.CreatedAt).TotalMinutes:0} min after the run was created, naming {reviewer}");
            // A cycle or two on, the run must still be waiting: the worker never cancels a run a person can approve.
            await ctx.Wait(TimeSpan.FromMinutes(2));
            var still = await r.Run(held.Id, ctx.Ct);
            ctx.Require(still.Status == "waiting" && (await t.Checks(sha, ctx.Ct)).Count == 0,
                $"cycle {held.Id} was still waiting, uncancelled, and nothing was tested meanwhile", still.Status);

            await r.SetReviewed(t, false, ctx.Ct);
            await r.CancelRun(held.Id, ctx.Ct);
            ctx.Step($"removed {t.Repo} from the reviewed targets and cancelled cycle {held.Id} by hand");
            await r.AwaitRun(held.Id, TimeSpan.FromMinutes(3), ctx.Ct);
            var check = await ctx.Tested(sha);
            var done = await t.AwaitCompleted(check.Id, TimeSpan.FromMinutes(10), ctx.Ct);
            ctx.Require(done.Conclusion == "success", $"the target's next cycle tested {Target.Short(sha)} and reported check {check.Id} as success");
        }
        finally
        {
            await r.SetReviewed(t, false, ctx.Ct);
            if (held is not null && !(await r.Run(held.Id, ctx.Ct)).Completed) await r.CancelRun(held.Id, ctx.Ct);
        }
    }
}
