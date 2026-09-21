using System.Text;
using System.Text.Json.Nodes;
using MainWatcher.Core;
using MainWatcher.Scenarios.Infra;

namespace MainWatcher.Scenarios.Sandbox;

/// <summary>A target's entry in the replica's <c>targets.yml</c>, as the suite writes it.</summary>
public sealed record TargetEntry(string Repo, int Timeout = 30, int PollInterval = 1, string[]? Notify = null, bool Enabled = true);

/// <summary>
/// The watcher replica, <c>main-watcher-sandbox/main-watcher</c>: its <c>watch.yml</c> cycles and sweeps, its sandbox
/// switches, its <c>targets.yml</c> and the <c>watcher-infra</c> alerts it raises. Several scenarios change the shared switch
/// variables and <c>targets.yml</c> at once, so each change is a read, an edit of one target's part, and a write, one at a time.
/// </summary>
public sealed class Replica(GitHub github, string repo)
{
    public const string WatchWorkflow = "watch.yml";
    public const string AlertLabel = "watcher-infra";

    /// <summary>The per-target switches (<see cref="SandboxSwitch"/>), one entry per target and value.</summary>
    public const string ExitAfter = "MW_SANDBOX_EXIT_AFTER";
    public const string RefuseCancel = "MW_SANDBOX_REFUSE_CANCEL";
    public const string QueueDeadline = "MW_QUEUE_DEADLINE_MINUTES";

    /// <summary>A plain list of targets whose cycles get a read-only Issues token (TS-S14 (c)).</summary>
    public const string ReadOnlyIssues = "MW_SANDBOX_READ_ONLY_ISSUES";

    public static readonly string[] Switches = [ExitAfter, RefuseCancel, QueueDeadline, ReadOnlyIssues];

    readonly SemaphoreSlim writes = new(1);
    readonly Dictionary<string, TargetEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    int lockLease = 10;

    public string Repo { get; } = repo;

    // ---- watch.yml ----

    /// <summary>Starts one cycle for a target, as the worker would, and returns its run.</summary>
    public async Task<long> DispatchCycle(Target target, CancellationToken ct, bool force = false) =>
        (await github.Post($"repos/{Repo}/actions/workflows/{WatchWorkflow}/dispatches", new
        {
            @ref = "main",
            inputs = new Dictionary<string, string> { ["target"] = target.Repo, ["force"] = force ? "true" : "false" },
            return_run_details = true
        }, ct))!["workflow_run_id"]!.GetValue<long>();

    /// <summary>Starts a sweep: every enabled target gets a cycle, which also looks for what only the sweep reports.</summary>
    public async Task<long> DispatchSweep(CancellationToken ct) =>
        (await github.Post($"repos/{Repo}/actions/workflows/{WatchWorkflow}/dispatches",
            new { @ref = "main", inputs = new Dictionary<string, string>(), return_run_details = true }, ct))!["workflow_run_id"]!.GetValue<long>();

    public async Task<Run> Run(long id, CancellationToken ct) => global::MainWatcher.Scenarios.Sandbox.Run.From((await github.Get($"repos/{Repo}/actions/runs/{id}", ct))!);

    public Task<Run> AwaitRun(long id, TimeSpan timeout, CancellationToken ct) =>
        Poll.Until($"watch run {id} to finish", timeout, async () => await Run(id, ct) is { Completed: true } run ? run : null, ct,
            TimeSpan.FromSeconds(15));

    /// <summary><c>watch.yml</c> runs created at or after <paramref name="since"/>, newest first.</summary>
    public async Task<List<Run>> WatchRuns(DateTimeOffset since, CancellationToken ct) =>
        (await github.All($"repos/{Repo}/actions/workflows/{WatchWorkflow}/runs?created=>={since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}",
            ct, "workflow_runs", 5)).Select(global::MainWatcher.Scenarios.Sandbox.Run.From).ToList();

    /// <summary>A target's cycles since a moment: dispatched runs are named <c>watch owner/repo</c>.</summary>
    public async Task<List<Run>> Cycles(Target target, DateTimeOffset since, CancellationToken ct) =>
        (await WatchRuns(since, ct)).Where(r => r.Title == $"watch {target.Repo}").ToList();

    /// <summary>The log of a run's cycle for <paramref name="target"/>: the one job of a dispatched cycle, or its leg of a sweep.</summary>
    public async Task<string> CycleLog(long runId, Target target, CancellationToken ct)
    {
        var jobs = (await github.All($"repos/{Repo}/actions/runs/{runId}/jobs?filter=latest", ct, "jobs", 2)).Select(Job.From).ToList();
        var leg = jobs.FirstOrDefault(j => j.Name == $"watch ({target.Repo})") ?? jobs.FirstOrDefault(j => j.Name.StartsWith("watch", StringComparison.Ordinal))
            ?? throw new ScenarioFailure($"watch run {runId} has no watch job");
        return await github.Text($"repos/{Repo}/actions/jobs/{leg.Id}/logs", ct);
    }

    /// <summary>
    /// Enables <c>watch.yml</c> and returns once GitHub reports it active, a few seconds later. A sweep dispatched a second after
    /// the enable call was accepted and never queued (2026-09-21): it stayed <c>queued</c> with no jobs, and GitHub refused to
    /// cancel it, "has not been queued yet".
    /// </summary>
    public async Task EnableWatch(CancellationToken ct)
    {
        await github.Put($"repos/{Repo}/actions/workflows/{WatchWorkflow}/enable", null, ct);
        await Poll.True($"{WatchWorkflow} to be active", TimeSpan.FromMinutes(2), async () =>
            (await github.Get($"repos/{Repo}/actions/workflows/{WatchWorkflow}", ct))!["state"]!.GetValue<string>() == "active",
            ct, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
    }

    /// <summary>
    /// Dispatches until a run is really queued: one that has jobs or has left <c>queued</c> within 3 minutes. A run GitHub
    /// accepted and never queued is left behind, since it cannot be cancelled, and dispatched again, up to 3 times.
    /// </summary>
    public async Task<long> Started(Func<Task<long>> dispatch, Log log, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var id = await dispatch();
            try
            {
                await Poll.True($"watch run {id} to be queued", TimeSpan.FromMinutes(3), async () =>
                    (await Run(id, ct)).Status != "queued"
                    || (await github.Get($"repos/{Repo}/actions/runs/{id}/jobs", ct))!["total_count"]!.GetValue<int>() > 0,
                    ct, TimeSpan.FromSeconds(10));
                return id;
            }
            catch (ScenarioFailure) when (attempt < 3)
            {
                log.Warn($"watch run {id} was accepted but never queued; dispatching again");
            }
        }
    }

    public Task DisableWatch(CancellationToken ct) => github.Put($"repos/{Repo}/actions/workflows/{WatchWorkflow}/disable", null, ct);

    // ---- sandbox switches ----

    /// <summary>Sets one target's values of a per-target switch, none clearing it, leaving other targets' entries alone.</summary>
    public Task SetSwitch(string name, Target target, string[] values, CancellationToken ct) => EditVariable(name, entries =>
    {
        entries.RemoveAll(e => e.StartsWith(target.Repo + "=", StringComparison.OrdinalIgnoreCase));
        entries.AddRange(values.Select(value => $"{target.Repo}={value}"));
    }, ct);

    /// <summary>Sets a whole variable, such as the queue deadline setting the worker is given too.</summary>
    public async Task SetVariable(string name, string value, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try
        {
            var current = await Variable(name, ct);
            if (current == value) return;
            if (current is null) await github.Post($"repos/{Repo}/actions/variables", new { name, value }, ct);
            else await github.Patch($"repos/{Repo}/actions/variables/{name}", new { name, value }, ct);
        }
        finally { writes.Release(); }
    }

    public async Task DeleteVariable(string name, CancellationToken ct)
    {
        if (await Variable(name, ct) is not null) await github.Delete($"repos/{Repo}/actions/variables/{name}", ct);
    }

    /// <summary>Adds a target to, or removes it from, the read-only Issues list.</summary>
    public Task SetReadOnlyIssues(Target target, bool on, CancellationToken ct) => EditVariable(ReadOnlyIssues, entries =>
    {
        entries.RemoveAll(e => e.Equals(target.Repo, StringComparison.OrdinalIgnoreCase));
        if (on) entries.Add(target.Repo);
    }, ct);

    /// <summary>Removes every switch entry naming a target, for a reset.</summary>
    public async Task ClearSwitches(Target target, CancellationToken ct)
    {
        foreach (var name in new[] { ExitAfter, RefuseCancel }) await SetSwitch(name, target, [], ct);
        await SetReadOnlyIssues(target, false, ct);
    }

    public async Task<string?> Variable(string name, CancellationToken ct) =>
        (await github.Find($"repos/{Repo}/actions/variables/{name}", ct))?["value"]?.GetValue<string>();

    async Task EditVariable(string name, Action<List<string>> edit, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try
        {
            var current = await Variable(name, ct);
            var list = (current ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            edit(list);
            var value = string.Join(',', list);
            if (value == (current ?? "")) return;
            if (value.Length == 0) await github.Delete($"repos/{Repo}/actions/variables/{name}", ct);
            else if (current is null) await github.Post($"repos/{Repo}/actions/variables", new { name, value }, ct);
            else await github.Patch($"repos/{Repo}/actions/variables/{name}", new { name, value }, ct);
        }
        finally { writes.Release(); }
    }

    // ---- targets.yml ----

    /// <summary>Writes the suite's target list: the pool, each with its default entry, and the sandbox <c>lock_lease</c>.</summary>
    public async Task WriteTargets(IEnumerable<TargetEntry> pool, int lease, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try
        {
            entries.Clear();
            foreach (var entry in pool) entries[entry.Repo] = entry;
            lockLease = lease;
            await PutTargets("Scenario suite: watch the target pool", ct);
        }
        finally { writes.Release(); }
    }

    /// <summary>Changes one target's entry, such as its <c>timeout</c>, <c>notify</c> list or <c>enabled</c>.</summary>
    public async Task EditTarget(Target target, Func<TargetEntry, TargetEntry> edit, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try
        {
            var before = entries[target.Repo];
            var after = edit(before);
            if (after == before) return;
            entries[target.Repo] = after;
            await PutTargets($"Scenario suite: {target.Name} {Describe(after)}", ct);
        }
        finally { writes.Release(); }
    }

    public TargetEntry Entry(Target target) => entries[target.Repo];

    public string TargetsYaml()
    {
        var yaml = new StringBuilder()
            .Append("# The scenario suite's target pool, written by sandbox/scenarios (MainWatcher#25). A 10-minute lock_lease\n")
            .Append("# makes a lapse take minutes (TS-S7), and poll_interval 1 makes each scenario take minutes.\n")
            .Append($"lock_lease: {lockLease}\n").Append("targets:\n");
        foreach (var entry in entries.Values.OrderBy(e => e.Repo, StringComparer.Ordinal))
            yaml.Append($"  - repo: {entry.Repo}\n")
                .Append("    test_command: dotnet test --no-restore\n")
                .Append("    results_glob: '**/TestResults/*.ctrf.json'\n")
                .Append($"    timeout: {entry.Timeout}\n")
                .Append($"    poll_interval: {entry.PollInterval}\n")
                .Append($"    notify: [{string.Join(", ", entry.Notify ?? [])}]\n")
                .Append($"    enabled: {(entry.Enabled ? "true" : "false")}\n");
        var text = yaml.ToString();
        TargetConfiguration.Parse(text);
        return text;
    }

    async Task PutTargets(string message, CancellationToken ct)
    {
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(TargetsYaml()));
        for (var attempt = 1; ; attempt++)
        {
            var sha = (await github.Find($"repos/{Repo}/contents/targets.yml?ref=main", ct))?["sha"]?.GetValue<string>();
            try
            {
                await github.Put($"repos/{Repo}/contents/targets.yml", new JsonObject
                {
                    ["message"] = message,
                    ["content"] = content,
                    ["sha"] = sha,
                    ["branch"] = "main"
                }, ct);
                return;
            }
            catch (GitHubException e) when (attempt < 4 && (int)e.Status is 409 or 422)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    static string Describe(TargetEntry e) =>
        $"timeout {e.Timeout}, notify [{string.Join(", ", e.Notify ?? [])}], {(e.Enabled ? "enabled" : "disabled")}";

    // ---- alerts ----

    public async Task<List<Issue>> Alerts(CancellationToken ct, string state = "open") =>
        (await github.All($"repos/{Repo}/issues?labels={AlertLabel}&state={state}&sort=created&direction=desc", ct, maxPages: 3))
            .Where(i => i["pull_request"] is null).Select(Issue.From).ToList();

    public async Task<List<Comment>> Comments(int number, CancellationToken ct) =>
        (await github.All($"repos/{Repo}/issues/{number}/comments", ct)).Select(Comment.From).ToList();

    /// <summary>
    /// The open alert titled <paramref name="title"/> if it was raised at or after <paramref name="since"/>: created then, or,
    /// since alerts de-duplicate by title, commented on then (ADR-012).
    /// </summary>
    public async Task<Issue?> Raised(string title, DateTimeOffset since, CancellationToken ct)
    {
        var alert = (await Alerts(ct)).Where(i => i.Title == title).MinBy(i => i.Number);
        if (alert is null) return null;
        if (alert.CreatedAt >= since.AddSeconds(-5)) return alert;
        return (await Comments(alert.Number, ct)).Any(c => c.CreatedAt >= since.AddSeconds(-5)) ? alert : null;
    }

    public Task<Issue> AwaitAlert(string title, DateTimeOffset since, TimeSpan timeout, CancellationToken ct) =>
        Poll.Until($"the alert \"{title}\"", timeout, () => Raised(title, since, ct), ct);

    /// <summary>The alert text: its body and every comment, since a repeat is a comment on the first.</summary>
    public async Task<string> AlertText(Issue alert, CancellationToken ct) =>
        alert.Body + "\n" + string.Join("\n", (await Comments(alert.Number, ct)).Select(c => c.Body));

    /// <summary>Closes the open alerts about a target, so the next scenario on it starts with none.</summary>
    public async Task CloseAlerts(Target target, string note, CancellationToken ct)
    {
        foreach (var alert in await Alerts(ct))
            if (Names(alert.Title, target.Repo))
            {
                await github.Post($"repos/{Repo}/issues/{alert.Number}/comments", new { body = note }, ct);
                await github.Patch($"repos/{Repo}/issues/{alert.Number}", new { state = "closed", state_reason = "completed" }, ct);
            }
    }

    /// <summary>Whether an alert title is about exactly this target, not one whose name it starts: titles end "on owner/repo".</summary>
    public static bool Names(string title, string target)
    {
        var at = title.IndexOf(target, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        var end = at + target.Length;
        return end == title.Length || title[end] is ')' or ' ' or '.' or ':';
    }

    // ---- hand-made locks (sandbox-lock.yml) ----

    /// <summary>Opens an App-authored lock by hand with the given lease, and returns it.</summary>
    public async Task<Issue> OpenHandMadeLock(Target target, int leaseHours, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow;
        var run = (await github.Post($"repos/{Repo}/actions/workflows/sandbox-lock.yml/dispatches", new
        {
            @ref = "main",
            inputs = new Dictionary<string, string> { ["target"] = target.Name, ["action"] = "open", ["lease_hours"] = leaseHours.ToString() },
            return_run_details = true
        }, ct))!["workflow_run_id"]!.GetValue<long>();
        var done = await AwaitRun(run, TimeSpan.FromMinutes(6), ct);
        if (done.Conclusion != "success") throw new ScenarioFailure($"sandbox-lock run {run} ended {done.Conclusion}");
        return await target.AwaitNewLock(since, TimeSpan.FromMinutes(2), ct);
    }

    /// <summary>Closes every open App-authored lock on a target, as the App, so no override is recorded.</summary>
    public async Task CloseHandMadeLocks(Target target, CancellationToken ct)
    {
        var run = (await github.Post($"repos/{Repo}/actions/workflows/sandbox-lock.yml/dispatches", new
        {
            @ref = "main",
            inputs = new Dictionary<string, string> { ["target"] = target.Name, ["action"] = "close", ["lease_hours"] = "4" },
            return_run_details = true
        }, ct))!["workflow_run_id"]!.GetValue<long>();
        var done = await AwaitRun(run, TimeSpan.FromMinutes(6), ct);
        if (done.Conclusion != "success") throw new ScenarioFailure($"sandbox-lock close run {run} ended {done.Conclusion}");
    }
}
