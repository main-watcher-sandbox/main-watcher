using System.Text.Json.Nodes;
using MainWatcher.Core;
using MainWatcher.Scenarios.Infra;
using MainWatcher.Scenarios.Sandbox;

namespace MainWatcher.Scenarios.Scenarios;

/// <summary>
/// TS-S8: credential scope. <c>sandbox/ts-s8-credential-scope.sh</c> proves each App is refused outside its scope, and a target
/// test run finds no Main Watcher key or token in its own environment.
/// </summary>
public sealed class CredentialScope : Scenario
{
    public override string[] Covers => ["TS-S8"];
    public override string Title => "Each App is refused outside its scope, and a target test run holds no Main Watcher credential";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(9);

    public override async Task Run(ScenarioContext ctx)
    {
        var root = Repository.Root();
        // Every line is kept, so the table is saved whole even when the script exits non-zero, which it does on any FAIL row.
        var lines = new List<string>();
        var exited = "";
        try
        {
            await Infra.Shell.Run(Infra.Shell.Bash, ["sandbox/ts-s8-credential-scope.sh"], ctx.Ct, root,
                progress: line => { lock (lines) lines.Add(line); });
        }
        catch (InvalidOperationException) { exited = " It exited non-zero."; }
        string output;
        lock (lines) output = string.Join('\n', lines);
        await File.WriteAllTextAsync(Path.Combine(RunInfo.OutDir, "ts-s8.md"), output, ctx.Ct);
        foreach (var fail in lines.Where(l => l.StartsWith("| FAIL |", StringComparison.Ordinal))) ctx.Log.Warn(fail);
        if (exited.Length > 0 && !output.Contains("| FAIL |", StringComparison.Ordinal))
            throw new ScenarioFailure($"ts-s8-credential-scope.sh failed before its table was complete (ts-s8.md).{exited}");
        var passes = output.Split('\n').Count(l => l.StartsWith("| PASS |", StringComparison.Ordinal));
        ctx.Require(output.Contains("TS-S8: every check passed.", StringComparison.Ordinal) && !output.Contains("| FAIL |", StringComparison.Ordinal)
            && !output.Contains("| SKIP |", StringComparison.Ordinal), $"the credential-scope script passed all {passes} checks, none skipped (ts-s8.md)");

        var sha = await ctx.Push("inspect the test run's environment", s => s["inspect_environment"] = true);
        var check = await ctx.Result(sha, "success");
        var files = await ctx.Target.Artifact(check.RunId!.Value, "main-watcher-ctrf", ctx.Ct);
        var test = files.Where(f => f.Key.EndsWith(".ctrf.json", StringComparison.Ordinal))
            .SelectMany(f => JsonNode.Parse(f.Value)!["results"]!["tests"]!.AsArray())
            .FirstOrDefault(n => n!["name"]!.GetValue<string>().EndsWith("NoMainWatcherKey", StringComparison.Ordinal));
        ctx.Require(test?["status"]?.GetValue<string>() == "passed",
            "the target test run inspected its environment and found no Main Watcher key or token (NoMainWatcherKey passed)", test?.ToJsonString());
    }
}

/// <summary>TS-S10: the lock issue notifies the <c>notify</c> team, and falls back to CODEOWNERS.</summary>
public sealed class TeamNotify : Scenario
{
    const string Team = "sandbox-owners";

    public override string[] Covers => ["TS-S10"];
    public override string Title => "A lock mentions the notify team, or the CODEOWNERS team, and the team is notified";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(26);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        var handle = $"@{SandboxOrg.Org}/{Team}";
        await ctx.Replica.EditTarget(t, e => e with { Notify = [$"{SandboxOrg.Org}/{Team}"] }, ctx.Ct);
        await Mentioned(ctx, handle, "the notify team", "Alpha");
        await ctx.Result(await ctx.PushGreen(), "success");

        await ctx.Replica.EditTarget(t, e => e with { Notify = [] }, ctx.Ct);
        await ctx.Result(await t.Commit(new Dictionary<string, string?> { [Target.CodeOwnersFile] = $"* {handle}\n" }, "CODEOWNERS: the sandbox team", ctx.Ct), "success");
        await Mentioned(ctx, handle, "the CODEOWNERS team", "Beta");
    }

    static async Task Mentioned(ScenarioContext ctx, string handle, string who, string test)
    {
        var since = DateTimeOffset.UtcNow;
        await ctx.Result(await ctx.PushFailing(test), "failure");
        var lockIssue = await ctx.Target.AwaitNewLock(since, TimeSpan.FromMinutes(3), ctx.Ct);
        ctx.Require(lockIssue.Body.StartsWith(handle, StringComparison.Ordinal), $"lock #{lockIssue.Number} opens by mentioning {who}, {handle}");
        ctx.Require(await ctx.Replica.Raised($"Lock issues on {ctx.Target.Repo} mention nobody", since, ctx.Ct) is null, "no \"mention nobody\" alert");
        var reason = await Poll.Until($"{ctx.Sandbox.Me}'s notification for lock #{lockIssue.Number}", TimeSpan.FromMinutes(4), async () =>
            (await ctx.Sandbox.GitHub.All($"notifications?all=true&since={since.AddMinutes(-1).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}", ctx.Ct, maxPages: 2))
                .FirstOrDefault(n => n["subject"]?["url"]?.GetValue<string>()?.EndsWith($"/{ctx.Target.Repo}/issues/{lockIssue.Number}", StringComparison.Ordinal) == true)
                ?["reason"]?.GetValue<string>(), ctx.Ct);
        ctx.Require(reason == "team_mention", $"{ctx.Sandbox.Me}, a member of the team, was notified for the team mention", reason);
    }
}

/// <summary>
/// TS-S17: an unlabelled PR whose gate passed while a slow second required check still runs is removed from the queue when a
/// lock opens, because the watcher re-runs its gate; the same holds for a gate still running when the lock opens.
/// </summary>
public sealed class QueuedBeforeLock : Scenario
{
    const int SlowCheck = 15;

    public override string[] Covers => ["TS-S17 (a)"];
    public override string Title => "Groups queued before a lock, their gate passed or still running, are re-checked and removed";
    public override TimeSpan Estimate => TimeSpan.FromMinutes(34);

    public override async Task Run(ScenarioContext ctx)
    {
        var t = ctx.Target;
        await ctx.Result(await ctx.Push($"a {SlowCheck}-minute second check", s => s["slow_check_minutes"] = SlowCheck), "success");
        await Round(ctx, "passed", "its gate had passed");

        await ctx.Result(await ctx.Push("pass", s => s["failing_tests"] = new JsonArray()), "success");
        await Poll.True("the lock to close", TimeSpan.FromMinutes(3), async () => (await t.Locks(ctx.Ct, "open")).Count == 0, ctx.Ct);
        await ctx.Result(await t.Commit(new Dictionary<string, string?> { [Target.GateFile] = ctx.Sandbox.Templates.Gate(lingerSeconds: 240) },
            "Gate: linger 4 minutes after deciding", ctx.Ct), "success");
        await Round(ctx, "running", "its gate was still running");
    }

    static async Task Round(ScenarioContext ctx, string name, string state)
    {
        var t = ctx.Target;
        var since = DateTimeOffset.UtcNow;
        var pr = await t.OpenPullRequest($"ts-s17a-{name}", ctx.Ct);
        await Poll.True($"#{pr.Number}'s own checks", TimeSpan.FromMinutes(6), async () =>
        {
            var runs = await t.GitHub.All($"repos/{t.Repo}/commits/{pr.HeadSha}/check-runs?filter=latest", ctx.Ct, "check_runs", 1);
            return runs.Count(r => r["name"]?.GetValue<string>() is "main-watcher-gate" or "sandbox-slow-check" && r["conclusion"]?.GetValue<string>() == "success") == 2;
        }, ctx.Ct, TimeSpan.FromSeconds(10));
        var red = await ctx.PushFailing("Alpha");
        await t.Enqueue(pr, ctx.Ct);
        var queued = DateTimeOffset.UtcNow;
        var gate = await Poll.Until($"#{pr.Number}'s gate run", TimeSpan.FromMinutes(5), async () =>
            (await t.GateRuns(pr.Number, queued.AddSeconds(-30), ctx.Ct)).LastOrDefault(), ctx.Ct, TimeSpan.FromSeconds(10));
        var lockIssue = await t.AwaitNewLock(since, TimeSpan.FromMinutes(10), ctx.Ct);
        // The first attempt's gate job, whichever attempt the run is on by now: when did it decide, against the lock's opening.
        var firstJob = (await t.Jobs(gate.Id, ctx.Ct, attempt: 1) ?? []).Single(j => j.Name == "main-watcher-gate");
        var before = firstJob.Completed && firstJob.CompletedAt < lockIssue.CreatedAt;
        ctx.Require(name == "passed" ? before && firstJob.Conclusion == "success" : !before,
            $"when lock #{lockIssue.Number} opened, #{pr.Number} was queued and {state}", firstJob.ToString());
        await t.AwaitRemovedFromQueue(pr, TimeSpan.FromMinutes(SlowCheck), ctx.Ct);
        var (run, log) = await GateLog.Verdict(ctx, pr, queued.AddSeconds(-30), attempt: 2);
        ctx.Require(run.Id == gate.Id && run.Attempt >= 2 && run.Conclusion == "failure" && GateLog.Has(log, $"Not labelled: #{pr.Number}"),
            $"the watcher re-ran #{pr.Number}'s gate, which failed, and the queue removed it before its slow check ended", run.ToString());
        var body = (await t.Issue(lockIssue.Number, ctx.Ct)).Body;
        ctx.Require(Markers.Field(body, "queue_swept") is { } swept && swept == Markers.Field(body, "sweep_required"),
            $"lock #{lockIssue.Number} records its queue sweep as finished");
        await ctx.Result(red, "failure");
    }
}
