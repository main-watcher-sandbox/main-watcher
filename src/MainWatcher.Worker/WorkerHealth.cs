namespace MainWatcher.Worker;

/// <summary>
/// Liveness for <c>/healthz</c>. The worker is live while cycles keep finishing, whatever their result: a cycle that fails on a
/// GitHub error is not fixed by a restart, but a hung one is.
/// </summary>
public sealed class WorkerHealth(TimeSpan checkPeriod, DateTimeOffset started)
{
    long lastFinishedTicks = started.UtcTicks;

    /// <summary>A cycle may run for <see cref="WorkerSettings.CycleTimeout"/>, and the next one starts a check period later.</summary>
    public TimeSpan Allowance { get; } = WorkerSettings.CycleTimeout + 2 * checkPeriod;

    public DateTimeOffset LastFinished => new(Interlocked.Read(ref lastFinishedTicks), TimeSpan.Zero);

    public CycleResult? LastResult { get; private set; }

    public void Finished(DateTimeOffset at, CycleResult? result)
    {
        LastResult = result;
        Interlocked.Exchange(ref lastFinishedTicks, at.UtcTicks);
    }

    public bool IsLive(DateTimeOffset now) => now - LastFinished <= Allowance;
}
