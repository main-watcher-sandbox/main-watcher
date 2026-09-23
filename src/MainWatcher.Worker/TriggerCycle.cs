using MainWatcher.Core;
using Microsoft.Extensions.Logging;

namespace MainWatcher.Worker;

/// <summary>What one cycle did.</summary>
public sealed record CycleResult(int Targets, int Dispatched, int Errors);

/// <summary>One cycle's counts, for <c>/healthz</c> and the log, and what it saw, for the health alerts.</summary>
public sealed record CycleReport(CycleResult Result, CycleObservations Observations);

/// <summary>
/// One trigger-worker cycle (ADR-010): read <c>targets.yml</c>, find each enabled target's work, and start <c>watch.yml</c> for it
/// through <c>mw-doorbell</c>, at most once per target.
/// </summary>
/// <param name="watcher">The <c>mw-observer</c> gateway for the watcher repo.</param>
/// <param name="doorbell">The <c>mw-doorbell</c> gateway for the watcher repo.</param>
/// <param name="observerFor">The <c>mw-observer</c> gateway for a target. Reuse one per target: it keeps the target's check snapshot.</param>
public sealed class TriggerCycle(IGitHubGateway watcher, IGitHubGateway doorbell, Func<string, IGitHubGateway> observerFor,
    string watcherRepo, string targetsPath, WorkFinder finder, ILogger log, Func<DateTimeOffset>? clock = null)
{
    /// <summary>How far back an unfinished <c>watch.yml</c> run is looked for. Its job times out after 15 minutes.</summary>
    public static readonly TimeSpan ActiveRunWindow = TimeSpan.FromHours(2);

    TargetConfiguration? config;

    public async Task<CycleReport> Run(CancellationToken ct)
    {
        var targets = (await Targets(ct)).Targets.Where(t => t.Enabled).ToArray();
        var cycles = await ActiveCycles(ct);
        var problems = new List<string>();
        var (stuck, unjudged) = cycles.Unfinished is { } unfinished
            ? await StopStuckRuns(unfinished, problems, ct) : ((IReadOnlyList<StuckRun>?)null, (IReadOnlyCollection<string>)[]);
        var pending = new List<PendingReport>();
        var sweeps = new List<PendingSweep>();
        var examined = new List<string>();
        int dispatched = 0, errors = 0;
        foreach (var target in targets)
        {
            try
            {
                if (cycles.Active.Contains(target.Repo))
                {
                    log.LogDebug("{Target}: a watch.yml run is already queued or running.", target.Repo);
                    continue;
                }
                var found = await finder.Find(target, observerFor(target.Repo), ct);
                examined.Add(target.Repo);
                // A queue sweep is timed from the generation it owes, and is recorded whether or not it is the reason for this
                // cycle: a report that keeps failing wins as the reason every time, and would hide the sweep for as long as it
                // lasted (ADR-016, PR #56 review). Saying nothing here means the cycle looked and found the sweep finished.
                if (found.Sweep is { Sweep: { } lockIssue } sweep)
                    sweeps.Add(new(target.Repo, lockIssue, sweep.Reason, sweep.Since ?? default));
                if (found.Work is not { } work) continue;
                // A report the Reporter owes is timed from the test job, so a Reporter that keeps failing becomes visible (ADR-013).
                if (work.Check is { } check) pending.Add(new(target.Repo, check, work.Reason, work.Since ?? default));
                // Never retried: a lost response may still have started the run, and the next cycle looks again.
                await doorbell.DispatchWorkflow(watcherRepo, WorkerSettings.WatchWorkflow,
                    new Dictionary<string, string> { ["target"] = target.Repo }, ct);
                dispatched++;
                log.LogInformation("{Target}: started {Workflow} because {Reason}.", target.Repo, WorkerSettings.WatchWorkflow, work.Reason);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                errors++;
                problems.Add($"{target.Repo}: {e.Message}");
                // A GitHub error needs no stack trace; anything else does.
                if (e is HttpRequestException) log.LogError("{Target}: cycle failed: {Message}", target.Repo, e.Message);
                else log.LogError(e, "{Target}: cycle failed: {Message}", target.Repo, e.Message);
            }
        }
        return new(new(targets.Length, dispatched, errors), new()
        {
            Problems = problems,
            Pending = pending,
            Sweeps = sweeps,
            Targets = targets.Select(t => t.Repo).ToArray(),
            Examined = examined,
            WatchRunStarted = dispatched > 0 || cycles.Active.Count > 0,
            WatchRunCompleted = cycles.Completed,
            StuckRuns = stuck,
            StuckUnjudged = unjudged
        });
    }

    /// <summary>
    /// ADR-020: stops each <c>watch.yml</c> run for a target that has not started by its deadline, unless it is held for a
    /// listed reviewer. Nothing is recorded: the stage comes from the run's own times on every cycle, and cancelling is
    /// idempotent. Once the run has completed it no longer marks its target active, so a later cycle dispatches again.
    /// </summary>
    /// <returns>
    /// The runs past their deadline, for the alerts, and the targets with a run whose gates could not be read: those are not
    /// judged this cycle, so their alerts keep their state rather than clearing and alerting again (PR #72 review).
    /// </returns>
    async Task<(IReadOnlyList<StuckRun>? Stuck, IReadOnlyCollection<string> Unjudged)> StopStuckRuns(IReadOnlyList<WorkflowRun> unfinished,
        List<string> problems, CancellationToken ct)
    {
        var now = Now();
        var stuck = new List<StuckRun>();
        var unjudged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in unfinished.Where(r => StuckWatchRuns.Due(r, now) && r.Title.StartsWith(RunNamePrefix, StringComparison.Ordinal)))
        {
            var target = run.Title[RunNamePrefix.Length..];
            IReadOnlyList<PendingDeployment> gates = [];
            if (run.Status == "waiting")
            {
                // Unread, a gate might list a reviewer, so nothing is cancelled; the failure counts towards the failing-cycles alert.
                try { gates = await watcher.PendingDeployments(watcherRepo, run.Id, ct); }
                catch (Exception e) when (e is (HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
                {
                    problems.Add($"{target}: could not read the gates of {WorkerSettings.WatchWorkflow} run {run.Id}: {e.Message}");
                    log.LogError("{Target}: could not read the gates of {Workflow} run {Run}: {Message}", target,
                        WorkerSettings.WatchWorkflow, run.Id, e.Message);
                    unjudged.Add(target);
                    continue;
                }
            }
            if (StuckWatchRuns.Judge(target, $"https://github.com/{watcherRepo}/actions/runs/{run.Id}", run, gates, now) is not { } found)
                continue;
            stuck.Add(found);
            if (found.Stage == StuckStage.Reviewers)
            {
                log.LogWarning("{Target}: {Workflow} run {Run} is waiting for {Reviewers}, so it is left alone.", target,
                    WorkerSettings.WatchWorkflow, run.Id, string.Join(", ", found.Reviewers));
                continue;
            }
            var force = found.Stage != StuckStage.Cancel;
            var refusal = await doorbell.CancelRun(watcherRepo, run.Id, force, ct);
            log.LogWarning("{Target}: {Workflow} run {Run} has been {State} since {Created:O}; {Action} {Outcome}.", target,
                WorkerSettings.WatchWorkflow, run.Id, run.Status, run.CreatedAt, force ? "force-cancelled" : "cancelled",
                refusal is null ? "accepted" : $"refused: {refusal}");
        }
        return (stuck, unjudged);
    }

    DateTimeOffset Now() => (clock ?? (() => DateTimeOffset.UtcNow))();

    async Task<TargetConfiguration> Targets(CancellationToken ct)
    {
        var yaml = await watcher.File(watcherRepo, targetsPath, ct);
        try
        {
            return config = TargetConfiguration.Parse(yaml ?? throw new InvalidDataException($"{targetsPath} does not exist on main."));
        }
        catch (Exception e) when (e is InvalidDataException or YamlDotNet.Core.YamlException)
        {
            // A bad file at startup is a configuration error. Later, the worker keeps the last good targets, as watch.yml would fail.
            if (config is null) throw new WorkerConfigurationException($"{watcherRepo}/{targetsPath} is invalid: {e.Message}");
            log.LogError("{Repo}/{Path} is invalid, so the previous targets are kept: {Message}", watcherRepo, targetsPath, e.Message);
            return config;
        }
    }

    /// <summary>
    /// Targets with a <c>watch.yml</c> run still queued or running, from its <c>run-name</c>, and when the newest run in the
    /// window finished. Such a run acts on the current state, so another dispatch would only queue a cycle with nothing left to
    /// do. A failed read skips nothing, and says nothing about completions: duplicates are harmless, and a silent alert is not.
    /// </summary>
    /// <returns>
    /// <c>Completed</c> is the newest completion time seen, not the fact that one was seen, so reading the same finished run on
    /// every cycle for the next two hours cannot keep pushing the "no run completed" clock forward. <c>Unfinished</c> is every
    /// run in the window not yet completed, and every run not yet started whatever its age, for ADR-020; it is null with
    /// <c>Completed</c> when either list could not be read. A run older than the window that has not started still marks its
    /// target active: it holds the target's concurrency group, and ADR-020 stops it.
    /// </returns>
    async Task<(HashSet<string> Active, DateTimeOffset? Completed, IReadOnlyList<WorkflowRun>? Unfinished)> ActiveCycles(CancellationToken ct)
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var since = Now() - ActiveRunWindow;
            var runs = await watcher.Runs(watcherRepo, WorkerSettings.WatchWorkflow, since, ct);
            var unstarted = await watcher.RunsIn(watcherRepo, WorkerSettings.WatchWorkflow, StuckWatchRuns.UnstartedStatuses, ct);
            var unfinished = runs.Where(r => r.Status != "completed").Concat(unstarted).DistinctBy(r => r.Id).ToArray();
            foreach (var run in unfinished)
                if (run.Title.StartsWith(RunNamePrefix, StringComparison.Ordinal))
                    active.Add(run.Title[RunNamePrefix.Length..]);
            // A run GitHub gives no update time for is dated by its creation: earlier than the truth, so it never hides a stall.
            return (active, runs.Where(r => r.Status == "completed").Select(r => r.UpdatedAt ?? r.CreatedAt)
                .DefaultIfEmpty().Max() is { Ticks: > 0 } newest ? newest : null, unfinished);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Could not read {Workflow} runs, so no target is skipped: {Message}", WorkerSettings.WatchWorkflow, e.Message);
            return (active, null, null);
        }
    }

    /// <summary><c>watch.yml</c>'s <c>run-name</c> is this prefix followed by the target.</summary>
    public const string RunNamePrefix = GitHubGateway.WatchRunPrefix;
}
