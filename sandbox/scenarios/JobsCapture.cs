using System.Text;
using System.Text.Json.Nodes;
using MainWatcher.Scenarios.Infra;
using MainWatcher.Scenarios.Sandbox;

namespace MainWatcher.Scenarios;

/// <summary>
/// <c>sandbox/run-scenarios.sh capture-jobs</c>: the evidence #65's rule rests on. It records how GitHub's jobs API reports a
/// <c>main-watcher</c> job around the moment it completes, for a run that finishes on its own, one cancelled during its tests,
/// one force-cancelled and one cancelled before it got a runner.
///
/// Each case commits its switches (and caller) to a side branch and runs the target's <c>main-watcher-tests.yml</c> by hand
/// from it. The watcher only tests <c>main</c>, and a run with no check run ID reports nothing, so the capture opens no check
/// run and no lock, and can run beside nothing else on that target. Every distinct jobs response is saved: every 10 s while the
/// job runs, every second from when its tests end or it is stopped until a minute after it completes, then every 15 s until
/// five minutes after, which shows whether steps left without a conclusion ever gain one.
/// </summary>
public static class JobsCapture
{
    const string Branch = "capture-jobs-65";
    static readonly TimeSpan Fast = TimeSpan.FromSeconds(1), Slow = TimeSpan.FromSeconds(10), Tail = TimeSpan.FromSeconds(15);

    public static async Task<int> Run(string[] args, string root, CancellationToken ct)
    {
        // Outside the suite's default pool (1 to 5, and 10), so a suite run cannot meet these hand-run tests.
        string repo = "sample-target-7";
        var repeat = 3;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--target" && i + 1 < args.Length) repo = args[++i];
            else if (args[i] == "--repeat" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n is >= 1 and <= 10) { repeat = n; i++; }
            else
            {
                Console.Error.WriteLine("Usage: sandbox/run-scenarios.sh capture-jobs [--target sample-target-7] [--repeat 3]");
                return 2;
            }
        }

        var outDir = Path.Combine(root, "sandbox", "scenarios", "out", $"capture-jobs-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(outDir);
        var log = new Log("capture", Path.Combine(outDir, "capture.log"));
        var token = Environment.GetEnvironmentVariable("GH_TOKEN") is { Length: > 0 } fromEnv ? fromEnv
            : (await Shell.Run("gh", ["auth", "token"], ct)).Trim();
        using var github = new GitHub(token);
        var templates = new Templates(root);
        var target = new Target(github, $"{SandboxOrg.Org}/{repo}", templates);
        log.Info($"Capturing jobs responses on {target}, into {outDir}");

        var cases = Enumerable.Range(1, repeat).Select(n => new Case($"completed-{n}", s => s["failing_tests"] = new JsonArray("Alpha"), Stop.None))
            .Append(new Case("cancelled", s => s["hang_test"] = true, Stop.Cancel))
            .Append(new Case("force-cancelled", s => s["hang_test"] = true, Stop.ForceCancel))
            .Append(new Case("no-runner", _ => { }, Stop.CancelQueued, RunsOn: "main-watcher-no-such-runner"))
            .ToList();
        var summary = new StringBuilder($"# Jobs API capture on {target} ({DateTimeOffset.UtcNow:yyyy-MM-dd})\n\n"
            + "Each row is one distinct response for the `main-watcher` job. Δ is the read time minus the job's `completed_at`.\n");
        var failed = false;
        try
        {
            foreach (var c in cases)
            {
                try { summary.Append(await Capture(target, templates, c, Path.Combine(outDir, c.Name), log, ct)); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    failed = true;
                    log.Warn($"{c.Name}: {e.Message}");
                    summary.Append($"\n## {c.Name}\n\nFailed: {e.Message}\n");
                }
            }
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(outDir, "summary.md"), summary.ToString(), CancellationToken.None);
            try { await target.DeleteBranch(Branch, CancellationToken.None); } catch (GitHubException) { }
        }
        log.Info($"{(failed ? "Finished with failures" : "Finished")}. Summary: {Path.Combine(outDir, "summary.md")}");
        return failed ? 1 : 0;
    }

    enum Stop { None, Cancel, ForceCancel, CancelQueued }

    sealed record Case(string Name, Action<JsonObject> Switches, Stop Stop, string? RunsOn = null);

    static async Task<string> Capture(Target target, Templates templates, Case c, string dir, Log log, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var switches = templates.DefaultSwitches();
        c.Switches(switches);
        try { await target.DeleteBranch(Branch, ct); } catch (GitHubException) { }
        var sha = await target.Commit(new Dictionary<string, string?>
        {
            [Target.SwitchesFile] = Templates.Format(switches),
            [Target.CallerFile] = templates.Caller(runsOn: c.RunsOn)
        }, $"capture-jobs: {c.Name}", ct, Branch);
        var runId = await target.Dispatch("main-watcher-tests.yml", new Dictionary<string, string> { ["sha"] = sha }, ct, Branch);
        log.Info($"{c.Name}: run {runId} on {Target.Short(sha)}");

        var responses = new List<(DateTimeOffset At, JsonObject Snapshot)>();
        string? last = null;
        DateTimeOffset? fastFrom = null, completedAt = null, stopped = null;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(40);
        var notes = new List<string>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            var jobs = await target.GitHub.All($"repos/{target.Repo}/actions/runs/{runId}/jobs?filter=latest", ct, "jobs", 2);
            var now = DateTimeOffset.UtcNow;
            var snapshot = JobsLog.Snapshot(jobs);
            var content = snapshot["jobs"]!.ToJsonString();
            if (content != last)
            {
                last = content;
                responses.Add((now, snapshot));
                await File.WriteAllTextAsync(Path.Combine(dir, $"{responses.Count:00}.json"),
                    snapshot.ToJsonString(new() { WriteIndented = true }), ct);
            }
            var job = jobs.FirstOrDefault(JobsLog.IsTestJob);
            var status = job?["status"]?.GetValue<string>();
            var steps = job?["steps"] as JsonArray ?? [];
            var test = steps.FirstOrDefault(s => s?["name"]?.GetValue<string>() == "main-watcher-test");
            completedAt ??= status == "completed" ? Time(job!["completed_at"]) ?? now : null;

            if (stopped is null && c.Stop != Stop.None && job is not null && status != "completed")
            {
                var ready = c.Stop == Stop.CancelQueued
                    ? status == "queued" && now - responses[0].At > TimeSpan.FromSeconds(30)
                    : test?["status"]?.GetValue<string>() == "in_progress";
                if (ready)
                {
                    if (c.Stop != Stop.CancelQueued) await Task.Delay(TimeSpan.FromSeconds(20), ct);
                    notes.Add(await Request(target, runId, c.Stop, log, c.Name, ct));
                    stopped = DateTimeOffset.UtcNow;
                    fastFrom = stopped;
                }
            }
            // The race #65 found is in the seconds after the tests end, so polling turns fast from there.
            if (fastFrom is null && (test?["status"]?.GetValue<string>() == "completed" || status == "completed")) fastFrom = now;

            if (completedAt is { } done && now - done > TimeSpan.FromMinutes(5)) break;
            var interval = completedAt is { } d && now - d > TimeSpan.FromMinutes(1) ? Tail : fastFrom is null ? Slow : Fast;
            await Task.Delay(interval, ct);
        }
        if (completedAt is null) notes.Add("The job had not completed after 40 minutes.");
        return Describe(c, runId, sha, responses, completedAt, notes);
    }

    /// <summary>Asks GitHub to stop the run, and says what it answered. A refused force-cancel is followed by a cancel and a second try.</summary>
    static async Task<string> Request(Target target, long runId, Stop stop, Log log, string name, CancellationToken ct)
    {
        if (stop != Stop.ForceCancel)
        {
            await target.CancelRun(runId, ct);
            log.Info($"{name}: cancelled run {runId}");
            return $"Cancelled at {DateTimeOffset.UtcNow:HH:mm:ss}Z.";
        }
        try
        {
            await target.ForceCancelRun(runId, ct);
            log.Info($"{name}: force-cancelled run {runId}");
            return $"Force-cancelled at {DateTimeOffset.UtcNow:HH:mm:ss}Z, accepted at once.";
        }
        catch (GitHubException first)
        {
            log.Warn($"{name}: force-cancel refused ({(int)first.Status}); cancelling, then force-cancelling again");
            await target.CancelRun(runId, ct);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            try
            {
                await target.ForceCancelRun(runId, ct);
                return $"A first force-cancel was refused with HTTP {(int)first.Status}; after a cancel, a second was accepted at {DateTimeOffset.UtcNow:HH:mm:ss}Z.";
            }
            catch (GitHubException second)
            {
                return $"Force-cancel refused with HTTP {(int)first.Status}, and again with HTTP {(int)second.Status} after a cancel: this case shows a cancel only.";
            }
        }
    }

    static string Describe(Case c, long runId, string sha, List<(DateTimeOffset At, JsonObject Snapshot)> responses, DateTimeOffset? completedAt,
        List<string> notes)
    {
        var text = new StringBuilder($"\n## {c.Name}\n\nRun {runId} on `{Target.Short(sha)}`. {string.Join(" ", notes)}\n\n"
            + "| # | Δ | job | steps | first, last | without a conclusion |\n|---|---|---|---|---|---|\n");
        for (var i = 0; i < responses.Count; i++)
        {
            var (at, snapshot) = responses[i];
            var job = (snapshot["jobs"] as JsonArray ?? []).OfType<JsonNode>().FirstOrDefault(JobsLog.IsTestJob);
            if (job is null)
            {
                text.Append($"| {i + 1:00} | {Delta(at, completedAt)} | no `main-watcher` job | | | |\n");
                continue;
            }
            var steps = (job["steps"] as JsonArray ?? []).OfType<JsonNode>().ToList();
            var open = steps.Where(s => s["conclusion"]?.GetValue<string>() is null)
                .Select(s => $"{s["name"]?.GetValue<string>()} ({s["status"]?.GetValue<string>()})").ToList();
            var ends = steps.Count == 0 ? "" : $"{steps[0]["name"]?.GetValue<string>()}, {steps[^1]["name"]?.GetValue<string>()}";
            text.Append($"| {i + 1:00} | {Delta(at, completedAt)} | {job["status"]?.GetValue<string>()}"
                + $"{(job["conclusion"]?.GetValue<string>() is { } conclusion ? $", {conclusion}" : "")} | {steps.Count} | {ends} "
                + $"| {(open.Count == 0 ? "none" : string.Join("; ", open))} |\n");
        }
        return text.ToString();
    }

    static string Delta(DateTimeOffset at, DateTimeOffset? completedAt) =>
        completedAt is { } done ? $"{(at - done).TotalSeconds:+0;-0;0} s" : "–";

    static DateTimeOffset? Time(JsonNode? node) => DateTimeOffset.TryParse(node?.GetValue<string>(), out var time) ? time : null;
}
