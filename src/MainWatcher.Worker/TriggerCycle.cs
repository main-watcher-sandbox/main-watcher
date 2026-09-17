using MainWatcher.Core;
using Microsoft.Extensions.Logging;

namespace MainWatcher.Worker;

/// <summary>What one cycle did.</summary>
public sealed record CycleResult(int Targets, int Dispatched, int Errors);

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

    public async Task<CycleResult> Run(CancellationToken ct)
    {
        var targets = (await Targets(ct)).Targets.Where(t => t.Enabled).ToArray();
        var active = await ActiveCycles(ct);
        int dispatched = 0, errors = 0;
        foreach (var target in targets)
        {
            try
            {
                if (active.Contains(target.Repo))
                {
                    log.LogDebug("{Target}: a watch.yml run is already queued or running.", target.Repo);
                    continue;
                }
                if (await finder.Find(target, observerFor(target.Repo), ct) is not { } reason) continue;
                // Never retried: a lost response may still have started the run, and the next cycle looks again.
                await doorbell.DispatchWorkflow(watcherRepo, WorkerSettings.WatchWorkflow,
                    new Dictionary<string, string> { ["target"] = target.Repo }, ct);
                dispatched++;
                log.LogInformation("{Target}: started {Workflow} because {Reason}.", target.Repo, WorkerSettings.WatchWorkflow, reason);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                errors++;
                // A GitHub error needs no stack trace; anything else does.
                if (e is HttpRequestException) log.LogError("{Target}: cycle failed: {Message}", target.Repo, e.Message);
                else log.LogError(e, "{Target}: cycle failed: {Message}", target.Repo, e.Message);
            }
        }
        return new(targets.Length, dispatched, errors);
    }

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
    /// Targets with a <c>watch.yml</c> run still queued or running, from its <c>run-name</c>. Such a run acts on the current
    /// state, so another dispatch would only queue a cycle with nothing left to do. A failed read skips nothing: duplicates are harmless.
    /// </summary>
    async Task<HashSet<string>> ActiveCycles(CancellationToken ct)
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var since = (clock ?? (() => DateTimeOffset.UtcNow))() - ActiveRunWindow;
            foreach (var run in await watcher.Runs(watcherRepo, WorkerSettings.WatchWorkflow, since, ct))
                if (run.Status != "completed" && run.Title.StartsWith(RunNamePrefix, StringComparison.Ordinal))
                    active.Add(run.Title[RunNamePrefix.Length..]);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Could not read {Workflow} runs, so no target is skipped: {Message}", WorkerSettings.WatchWorkflow, e.Message);
        }
        return active;
    }

    /// <summary><c>watch.yml</c>'s <c>run-name</c> is this prefix followed by the target.</summary>
    public const string RunNamePrefix = "watch ";
}
