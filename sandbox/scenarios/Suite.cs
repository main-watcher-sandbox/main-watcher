using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MainWatcher.Scenarios.Infra;
using MainWatcher.Scenarios.Sandbox;

namespace MainWatcher.Scenarios;

/// <summary>How one unit ended.</summary>
public sealed record UnitResult(Scenario Scenario, string Target, bool Passed, string? Failure, DateTimeOffset Started,
    DateTimeOffset Ended, IReadOnlyList<string> Evidence)
{
    public TimeSpan Duration => Ended - Started;
}

/// <summary>
/// Runs the selected units: the outage and early units at once, each on a target of its own, and the rest from the pool as
/// targets come free, each followed by a reset of its target.
/// </summary>
public sealed class Suite(SandboxOrg sandbox, IReadOnlyList<Scenario> units, string outDir, Log log)
{
    readonly ConcurrentBag<UnitResult> results = [];
    readonly List<Scenario> pending = [];
    readonly Lock pick = new();

    public IReadOnlyCollection<UnitResult> Results => results;

    public async Task Run(CancellationToken ct)
    {
        sandbox.Outage.Configure(log, ct);
        var free = new Queue<Target>(sandbox.Pool);
        var lanes = new List<Task>();
        var fixedUnits = units.Where(u => u.Phase != Phase.Pool).ToList();
        pending.AddRange(units.Where(u => u.Phase == Phase.Pool).OrderByDescending(u => u.Estimate.Ticks));

        foreach (var unit in fixedUnits.Where(u => u.Phase == Phase.Outage)) sandbox.Outage.Join(unit.Name);
        foreach (var unit in fixedUnits)
        {
            var target = unit.Pinned(sandbox) ?? NextFree(free) ?? throw new InvalidOperationException(
                $"The pool has too few targets for the units that need their own ({fixedUnits.Count}).");
            RemoveFree(free, target);
            lanes.Add(Lane(target, unit, ct));
        }
        while (free.Count > 0) lanes.Add(Lane(free.Dequeue(), null, ct));
        await Task.WhenAll(lanes);
    }

    Target? NextFree(Queue<Target> free)
    {
        // The short-queue-deadline target is kept for the unit that needs it, and pool units, while one is free.
        var candidate = free.FirstOrDefault(t => t != sandbox.QueueDeadlineTarget) ?? free.FirstOrDefault();
        return candidate;
    }

    static void RemoveFree(Queue<Target> free, Target target)
    {
        var rest = free.Where(t => t != target).ToList();
        free.Clear();
        foreach (var t in rest) free.Enqueue(t);
    }

    async Task Lane(Target target, Scenario? first, CancellationToken ct)
    {
        var laneLog = new Log(target.Name, Path.Combine(outDir, $"{target.Name}.log"));
        if (first is not null && !await RunUnit(first, target, laneLog, ct)) return;
        while (!ct.IsCancellationRequested)
        {
            var next = Next(target, out var waiting);
            if (next is null)
            {
                if (!waiting) return;
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                continue;
            }
            if (!await RunUnit(next, target, laneLog, ct)) return;
        }
    }

    /// <summary>The next unit this target can run now; <paramref name="waiting"/> says whether one may become runnable later.</summary>
    Scenario? Next(Target target, out bool waiting)
    {
        lock (pick)
        {
            var mine = pending.Where(u => u.Pinned(sandbox) is null || u.Pinned(sandbox) == target).ToList();
            waiting = mine.Count > 0;
            var runnable = mine.Where(u => !u.NeedsWorker || sandbox.Outage.WorkerUp).ToList();
            var next = runnable.FirstOrDefault(u => u.Pinned(sandbox) == target) ?? runnable.FirstOrDefault();
            if (next is not null) pending.Remove(next);
            return next;
        }
    }

    /// <summary>Runs one unit and resets its target. False when the target could not be reset, which retires the lane.</summary>
    async Task<bool> RunUnit(Scenario unit, Target target, Log laneLog, CancellationToken ct)
    {
        var file = Path.Combine(outDir, $"{Slug(unit.Name)}.log");
        var unitLog = new Log(unit.Name, file);
        unitLog.Info($"{unit.Title} — on {target.Repo}");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromTicks(Math.Max(unit.Estimate.Ticks * 3, TimeSpan.FromMinutes(40).Ticks)));
        var context = new ScenarioContext(sandbox, target, unitLog, limit.Token);
        string? failure = null;
        try { await unit.Run(context); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            failure = e is ScenarioFailure ? e.Message
                : e is OperationCanceledException ? $"Did not finish within {TimeSpan.FromTicks(Math.Max(unit.Estimate.Ticks * 3, TimeSpan.FromMinutes(40).Ticks)).TotalMinutes:0} min."
                : $"{e.GetType().Name}: {e.Message}";
            unitLog.Warn($"FAILED: {failure}");
            if (e is not ScenarioFailure and not OperationCanceledException) unitLog.Warn(e.ToString());
        }
        finally
        {
            if (unit.Phase == Phase.Outage || unit.Phase == Phase.Early) sandbox.Outage.Leave(unit.Name);
        }
        var result = new UnitResult(unit, target.Repo, failure is null, failure, context.Started, DateTimeOffset.UtcNow, context.Evidence);
        results.Add(result);
        unitLog.Info(failure is null ? $"PASSED in {result.Duration.TotalMinutes:0.0} min" : $"FAILED after {result.Duration.TotalMinutes:0.0} min");

        try
        {
            // A worker-needing reset during the outage would only time out, so it waits for the worker.
            await sandbox.Outage.WaitForWorker(ct);
            await sandbox.Reset(target, laneLog, ct);
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            laneLog.Warn($"Could not reset {target.Repo}, so it takes no more units: {e.Message}");
            return false;
        }
    }

    static string Slug(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    /// <summary>The report: a table of units, then each unit's evidence or failure.</summary>
    public string Report(string commit, TimeSpan duration, IReadOnlyList<Scenario> notRun)
    {
        var passed = results.Count(r => r.Passed);
        var text = new StringBuilder()
            .AppendLine($"# Scenario suite run — MainWatcher@{commit[..Math.Min(12, commit.Length)]}")
            .AppendLine()
            .AppendLine($"{passed} of {results.Count} units passed in {duration.TotalMinutes:0} min, on {sandbox.Pool.Count} targets. "
                + $"GitHub API calls: {sandbox.GitHub.Calls}.")
            .AppendLine();
        if (sandbox.Outage.Began is { } began)
            text.AppendLine($"Watcher outage: {began:HH:mm:ss}Z to {sandbox.Outage.Ended:HH:mm:ss}Z, ended by sweep run {sandbox.Outage.SweepRun}.").AppendLine();
        text.AppendLine("| Result | Scenarios | Target | Minutes |").AppendLine("| --- | --- | --- | --- |");
        foreach (var r in results.OrderBy(r => r.Started))
            text.AppendLine($"| {(r.Passed ? "PASS" : "FAIL")} | {r.Scenario.Name} | {r.Target} | {r.Duration.TotalMinutes:0.0} |");
        foreach (var unit in notRun) text.AppendLine($"| NOT RUN | {unit.Name} | | |");
        foreach (var r in results.OrderBy(r => r.Started))
        {
            text.AppendLine().AppendLine($"## {r.Scenario.Name}: {r.Scenario.Title}").AppendLine()
                .AppendLine($"{(r.Passed ? "Passed" : "Failed")} on `{r.Target}`, {r.Started:HH:mm:ss}Z to {r.Ended:HH:mm:ss}Z.").AppendLine();
            foreach (var line in r.Evidence) text.AppendLine($"- {line}");
            if (r.Failure is not null) text.AppendLine().AppendLine($"**Failure:** {r.Failure}");
        }
        return text.ToString();
    }

    public string Json(string commit, TimeSpan duration) => JsonSerializer.Serialize(new
    {
        commit,
        minutes = Math.Round(duration.TotalMinutes, 1),
        units = results.OrderBy(r => r.Started).Select(r => new
        {
            scenarios = r.Scenario.Covers,
            target = r.Target,
            passed = r.Passed,
            failure = r.Failure,
            started = r.Started,
            minutes = Math.Round(r.Duration.TotalMinutes, 1),
            evidence = r.Evidence
        })
    }, new JsonSerializerOptions { WriteIndented = true });

}
