using System.Text.Json.Nodes;
using MainWatcher.Scenarios.Infra;
using MainWatcher.Scenarios.Sandbox;

namespace MainWatcher.Scenarios;

/// <summary>When in the run a scenario starts, which decides what it may rely on.</summary>
public enum Phase
{
    /// <summary>At the start, on a target of its own: it takes part in the watcher outage.</summary>
    Outage,

    /// <summary>At the start, on a target of its own: long, and needing no worker once its test job has started.</summary>
    Early,

    /// <summary>From the shared pool, whenever a target is free and the worker is up if it needs it.</summary>
    Pool
}

/// <summary>
/// One unit of the suite: one or more TS-001 scenarios run in order on one target. It prepares what it needs, acts, asserts
/// on what GitHub shows, and leaves the rest to the reset that follows it.
/// </summary>
public abstract class Scenario
{
    /// <summary>The TS-001 scenarios this unit covers, such as "TS-S16 (a)".</summary>
    public abstract string[] Covers { get; }

    public abstract string Title { get; }

    /// <summary>A rough duration, longest first being how the pool is ordered.</summary>
    public abstract TimeSpan Estimate { get; }

    public virtual Phase Phase => Phase.Pool;

    /// <summary>Whether it needs the trigger worker. Units that do not may run during the outage.</summary>
    public virtual bool NeedsWorker => true;

    /// <summary>A target it must run on, when a setting ties it to one.</summary>
    public virtual Target? Pinned(SandboxOrg sandbox) => null;

    public string Name => string.Join(", ", Covers);

    public abstract Task Run(ScenarioContext ctx);
}

/// <summary>What a running scenario works with, and the evidence it records.</summary>
public sealed class ScenarioContext(SandboxOrg sandbox, Target target, Log log, CancellationToken ct)
{
    public SandboxOrg Sandbox { get; } = sandbox;
    public Target Target { get; } = target;
    public Replica Replica => Sandbox.Replica;
    public Log Log { get; } = log;
    public CancellationToken Ct { get; } = ct;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    /// <summary>What was checked and held, in order, for the report.</summary>
    public List<string> Evidence { get; } = [];

    /// <summary>Records something that held.</summary>
    public void Pass(string what)
    {
        Evidence.Add(what);
        Log.Info("✓ " + what);
    }

    /// <summary>Fails the scenario unless <paramref name="condition"/> holds; records it if it does.</summary>
    public void Require(bool condition, string what, string? seen = null)
    {
        if (!condition) throw new ScenarioFailure(seen is null ? $"Expected: {what}" : $"Expected: {what}. Seen: {seen}");
        Pass(what);
    }

    public void Step(string what) => Log.Info(what);

    // ---- common steps ----

    /// <summary>Commits new switches and returns the new head.</summary>
    public async Task<string> Push(string message, Action<JsonObject> edit, IReadOnlyDictionary<string, string?>? alsoFiles = null)
    {
        var sha = await Target.SetSwitches(edit, message, Ct, alsoFiles);
        Step($"pushed {Target.Short(sha)}: {message}");
        return sha;
    }

    /// <summary>Commits switches that make the named tests fail, and returns the new head.</summary>
    public Task<string> PushFailing(params string[] tests) =>
        Push($"fail {string.Join(", ", tests)}", s => s["failing_tests"] = new JsonArray(tests.Select(t => (JsonNode)t).ToArray()));

    /// <summary>Commits the default switches, which pass.</summary>
    public Task<string> PushGreen(string message = "pass") =>
        Push(message, s => { foreach (var (key, value) in Sandbox.Templates.DefaultSwitches()) s[key] = value?.DeepClone(); });

    /// <summary>Waits for the head's result, and returns its check run.</summary>
    public Task<CheckRun> Result(string sha, string conclusion, int minutes = 12) =>
        Target.AwaitHeadResult(sha, conclusion, TimeSpan.FromMinutes(minutes), Ct);

    /// <summary>Waits for the worker to start a test of a head, and returns the check run with its target run.</summary>
    public async Task<CheckRun> Tested(string sha, int index = 1, int minutes = 6)
    {
        var check = await Target.AwaitCheck(sha, TimeSpan.FromMinutes(minutes), Ct, index);
        return await Target.AwaitLinked(check.Id, TimeSpan.FromMinutes(3), Ct);
    }

    public Task Wait(TimeSpan span) => Task.Delay(span, Ct);
}
