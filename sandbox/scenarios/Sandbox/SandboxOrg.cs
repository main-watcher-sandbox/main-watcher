using MainWatcher.Core;
using MainWatcher.Scenarios.Infra;

namespace MainWatcher.Scenarios.Sandbox;

/// <summary>
/// The sandbox the suite drives (TS-001 §3): the replica, the worker, the public gate repo and the target pool, and how a
/// target is put back to a clean state between scenarios.
/// </summary>
public sealed class SandboxOrg(GitHub github, Templates templates, Replica replica, Worker worker, IReadOnlyList<Target> pool, string me)
{
    public const string Org = "main-watcher-sandbox";
    public const string BotLogin = "main-watcher[bot]";
    public const string MainWatcherApp = "main-watcher";
    public const string LockLabel = "main-broken";
    public const string FixLabel = "fixes-main";

    /// <summary>The sandbox <c>lock_lease</c>, in minutes: short enough for a lapse to happen inside a scenario (TS-S7).</summary>
    public const int LockLease = 10;

    /// <summary>The queue deadline the target <see cref="QueueDeadlineTarget"/> is given (TS-S16 (g)).</summary>
    public const int ShortQueueDeadline = 10;

    public GitHub GitHub { get; } = github;
    public Templates Templates { get; } = templates;
    public Replica Replica { get; } = replica;
    public Worker Worker { get; } = worker;
    public IReadOnlyList<Target> Pool { get; } = pool;

    /// <summary>The person running the suite, who is each target's <c>notify</c> list and a member of the sandbox team.</summary>
    public string Me { get; } = me;

    /// <summary>
    /// The one target with a shortened queue deadline. The worker reads the deadline from its environment, which only a
    /// restart changes, so it is set once, in the sandbox overlay, for this target alone.
    /// </summary>
    public Target QueueDeadlineTarget => Pool[^1];

    public Outage Outage { get; } = new(replica, worker);

    public TargetEntry DefaultEntry(Target target) => new(target.Repo, Notify: [Me]);

    /// <summary>
    /// Puts a target back as a scenario expects to find it: no open pull request, no switch naming it, its default
    /// <c>targets.yml</c> entry, every file as seeded, no run in progress, <c>main</c> green, no open lock, every closed lock
    /// reconciled and no open alert about it.
    /// </summary>
    public async Task Reset(Target target, Log log, CancellationToken ct)
    {
        foreach (var number in await target.OpenPullRequests(ct)) await target.ClosePullRequest(number, ct);
        await Replica.ClearSwitches(target, ct);
        await Replica.EditTarget(target, _ => DefaultEntry(target), ct);

        foreach (var workflow in new[] { "main-watcher-tests.yml", "sandbox-hold.yml" })
            foreach (var run in (await target.Runs(workflow, DateTimeOffset.UtcNow.AddDays(-1), ct)).Where(r => !r.Completed))
            {
                log.Info($"reset: cancelling {run}");
                try { await target.CancelRun(run.Id, ct); } catch (GitHubException) { }
            }
        await Poll.True($"runs on {target} to stop", TimeSpan.FromMinutes(12), async () =>
            (await target.Runs("main-watcher-tests.yml", DateTimeOffset.UtcNow.AddDays(-1), ct)).All(r => r.Completed), ct);

        var files = new Dictionary<string, string?>();
        var defaults = Templates.DefaultSwitches();
        if (!Templates.SameSwitches(await target.File(Target.SwitchesFile, ct), defaults)) files[Target.SwitchesFile] = Templates.Format(defaults);
        if (!Templates.Same(await target.File(Target.CallerFile, ct), Templates.Caller())) files[Target.CallerFile] = Templates.Caller();
        if (!Templates.Same(await target.File(Target.GateFile, ct), Templates.Gate())) files[Target.GateFile] = Templates.Gate();
        if (await target.File(Target.CodeOwnersFile, ct) is not null) files[Target.CodeOwnersFile] = null;
        foreach (var probe in await GitHub.Find($"repos/{target.Repo}/contents/{Target.ProbeFolder}?ref=main", ct) as System.Text.Json.Nodes.JsonArray ?? [])
            files[probe!["path"]!.GetValue<string>()] = null;

        var head = await target.Head(ct);
        var newest = (await target.Checks(head, ct)).LastOrDefault();
        var locked = (await target.Locks(ct, "open")).Count > 0;
        // A lock closes on a green result, and a head that already has one gets no other, so a lock still open needs a new head.
        if (files.Count == 0 && (locked || newest is { Completed: true, Conclusion: not "success" }))
            files[$"{Target.ProbeFolder}/reset.md"] = $"Reset at {DateTimeOffset.UtcNow:O}\n";
        if (files.Count > 0)
        {
            head = await target.Commit(files, "Scenario suite: reset the target", ct);
            log.Info($"reset: committed {Target.Short(head)} ({string.Join(", ", files.Keys)})");
        }
        await target.AwaitHeadResult(head, "success", TimeSpan.FromMinutes(15), ct);
        await Poll.True($"no open lock on {target}", TimeSpan.FromMinutes(5), async () => (await target.Locks(ct, "open")).Count == 0, ct);
        try
        {
            // Reconciliation carries on after a lock closes (ADR-015), and would otherwise report into the next scenario.
            await Poll.True($"every closed lock on {target} to be reconciled", TimeSpan.FromMinutes(8), async () =>
                (await target.Locks(ct, "closed")).Where(l => l.ClosedAt > DateTimeOffset.UtcNow.AddDays(-2))
                .All(l => Markers.Field(l.Body, "reconciled") == "complete"), ct);
        }
        catch (ScenarioFailure e) { log.Warn($"reset: {e.Message}"); }
        await Replica.CloseAlerts(target, "Closed by the scenario suite while resetting this target.", ct);
    }
}
