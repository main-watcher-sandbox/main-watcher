using MainWatcher.Scenarios.Infra;

namespace MainWatcher.Scenarios.Sandbox;

/// <summary>
/// The one watcher outage of a suite run, shared by the scenarios that need the trigger worker scaled to zero and the hourly
/// sweep held off (TS-S7, TS-S11, TS-S15, TS-S17 (b)). Each prepares its target while the worker runs, then says it is ready;
/// once all are, the worker is stopped and <c>watch.yml</c> disabled, so GitHub's schedule cannot start a sweep either. Each
/// does its part and says it is done; once all are, <c>watch.yml</c> is enabled, one sweep is dispatched and awaited — the
/// hourly backup the worker's absence is meant to be caught by — and only then is the worker started again.
/// </summary>
public sealed class Outage(Replica replica, Worker worker)
{
    readonly Lock gate = new();
    readonly SemaphoreSlim dispatches = new(1);
    readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<long> restored = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly HashSet<string> participants = [];
    readonly HashSet<string> ready = [];
    readonly HashSet<string> done = [];
    Log? log;
    CancellationToken ct;

    /// <summary>When the worker stopped, and when it was started again.</summary>
    public DateTimeOffset? Began { get; private set; }
    public DateTimeOffset? Ended { get; private set; }

    /// <summary>The sweep dispatched at the end of the outage.</summary>
    public long? SweepRun { get; private set; }

    /// <summary>Why the outage could not happen, if it failed; every participant then fails with it.</summary>
    public Exception? Failure { get; private set; }

    public Task Started => started.Task;
    public Task Restored => restored.Task;

    /// <summary>Whether any scenario takes part, and so whether the suite must wait for the outage to end.</summary>
    public bool Planned { get { lock (gate) return participants.Count > 0; } }

    /// <summary>Whether the worker is up: no outage planned, or the outage over.</summary>
    public bool WorkerUp => !Planned || restored.Task.IsCompleted;

    /// <summary>Returns once the worker is up: at once when no outage is planned, else when it has ended.</summary>
    public Task WaitForWorker(CancellationToken token) => Planned ? restored.Task.WaitAsync(token) : Task.CompletedTask;

    public void Configure(Log suiteLog, CancellationToken token)
    {
        log = suiteLog;
        ct = token;
    }

    public void Join(string participant) { lock (gate) participants.Add(participant); }

    /// <summary>Says a participant has prepared its target, without waiting for the others.</summary>
    public void Ready(string participant) => Mark(ready, participant);

    /// <summary>Says a participant is ready and waits for the worker to be stopped.</summary>
    public async Task Begin(string participant)
    {
        Ready(participant);
        await started.Task.WaitAsync(ct);
        if (Failure is not null) throw new ScenarioFailure($"The watcher outage could not start: {Failure.Message}");
    }

    /// <summary>Says a participant has done its part, without waiting for the others.</summary>
    public void Done(string participant) => Mark(done, participant);

    /// <summary>Says a participant has done its part and waits for the watcher to be restored. Returns the restoring sweep.</summary>
    public async Task<long> End(string participant)
    {
        Done(participant);
        var sweep = await restored.Task.WaitAsync(ct);
        if (Failure is not null) throw new ScenarioFailure($"The watcher could not be restored: {Failure.Message}");
        return sweep;
    }

    /// <summary>A participant that stopped early still counts, so the others are not left waiting for it.</summary>
    public void Leave(string participant)
    {
        Mark(ready, participant);
        Mark(done, participant);
    }

    /// <summary>
    /// One cycle for one target during the outage, as a person would start it by hand. <c>watch.yml</c> is enabled just long
    /// enough for the dispatched run to be queued.
    /// </summary>
    public async Task<long> Dispatch(Target target)
    {
        await dispatches.WaitAsync(ct);
        try
        {
            await replica.EnableWatch(ct);
            try { return await replica.Started(() => replica.DispatchCycle(target, ct), log!, ct); }
            finally { await replica.DisableWatch(ct); }
        }
        finally { dispatches.Release(); }
    }

    void Mark(HashSet<string> set, string participant)
    {
        bool begin, end;
        lock (gate)
        {
            if (!set.Add(participant)) return;
            begin = set == ready && ready.IsSupersetOf(participants) && !started.Task.IsCompleted;
            end = set == done && done.IsSupersetOf(participants) && started.Task.IsCompleted && !restored.Task.IsCompleted;
        }
        if (begin) _ = Task.Run(Stop);
        if (end) _ = Task.Run(Restore);
    }

    async Task Stop()
    {
        try
        {
            log!.Info("Outage: stopping the trigger worker and disabling watch.yml.");
            await replica.DisableWatch(ct);
            await worker.Scale(0, ct);
            await AwaitInFlightCycles();
            Began = DateTimeOffset.UtcNow;
            log.Info("Outage: begun.");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Failure = e;
            log!.Warn($"Outage: could not begin: {e.Message}");
        }
        started.TrySetResult();
        // Every participant may already be done (each left early), so the end is checked again here.
        bool end;
        lock (gate) end = done.IsSupersetOf(participants);
        if (end) _ = Task.Run(Restore);
    }

    /// <summary>
    /// Waits for the <c>watch.yml</c> runs the worker started before it stopped. Disabling the workflow stops none of them, and
    /// one that renews a lease after the outage began breaks a scenario waiting for that lease to lapse (2026-09-24: cycles
    /// dispatched 3 s before the outage renewed two locks). A run still <c>queued</c> after 10 minutes is one GitHub accepted
    /// and never queued (<see cref="Replica.Started"/>); it cannot run, so it is left behind.
    /// </summary>
    async Task AwaitInFlightCycles()
    {
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        var seen = new HashSet<long>();
        while (true)
        {
            var open = (await replica.WatchRuns(since, ct)).Where(r => !r.Completed).ToList();
            var running = open.Where(r => r.Status != "queued").ToList();
            if (open.Count == 0) return;
            if (DateTimeOffset.UtcNow >= deadline)
            {
                if (running.Count > 0)
                    throw new InvalidOperationException($"watch.yml runs still running 10 minutes after the worker stopped: {string.Join(", ", running.Select(r => r.Id))}");
                log!.Warn($"Outage: leaving behind watch.yml runs that never left queued: {string.Join(", ", open.Select(r => r.Id))}.");
                return;
            }
            foreach (var run in open.Where(r => seen.Add(r.Id)))
                log!.Info($"Outage: waiting for watch.yml run {run.Id} ({run.Title}), started before the worker stopped.");
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    async Task Restore()
    {
        if (restored.Task.IsCompleted) return;
        long sweep = 0;
        try
        {
            log!.Info("Outage: enabling watch.yml and dispatching the sweep that restores the watcher.");
            await replica.EnableWatch(ct);
            if (Failure is null)
            {
                sweep = await replica.Started(() => replica.DispatchSweep(ct), log, ct);
                SweepRun = sweep;
                var run = await replica.AwaitRun(sweep, TimeSpan.FromMinutes(20), ct);
                log.Info($"Outage: sweep {run}.");
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Failure ??= e;
            log!.Warn($"Outage: the sweep failed: {e.Message}");
        }
        try
        {
            await worker.Scale(1, ct);
            Ended = DateTimeOffset.UtcNow;
            log!.Info("Outage: the trigger worker is running again.");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Failure ??= e;
            log!.Warn($"Outage: could not start the worker: {e.Message}");
        }
        restored.TrySetResult(sweep);
    }
}
