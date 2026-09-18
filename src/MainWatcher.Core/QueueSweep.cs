namespace MainWatcher.Core;

/// <summary>What the sweep does about one gate run of a group still in the queue (ADR-016 point 1).</summary>
public enum GateRunHandling
{
    /// <summary>It started after the cut-off, so it already read the lock, or it did not pass, so nothing is holding merge open.</summary>
    Leave,
    /// <summary>It passed before the cut-off, so the group is waiting on a verdict taken without the lock: re-run it.</summary>
    Rerun,
    /// <summary>It started before the cut-off and is still running, so it may yet report "no lock". Re-run it once it completes.</summary>
    Wait
}

/// <summary>
/// The queue sweep (ADR-016): a group whose gate passed before a lock existed would otherwise merge onto a red <c>main</c> as
/// soon as its other required checks finish, with no outage involved for ADR-008 to describe. So opening a lock, and renewing
/// one whose lease had lapsed, records an obligation to re-run the gate for the groups still queued.
/// <para>
/// This is the rule the Planner, which sweeps, and the trigger worker, which flags the obligation so a cycle runs at all, both
/// read (TS-U12). The obligation is a generation, not a flag: <c>sweep_required</c> is written in the same issue write as its
/// cause, and <c>queue_swept</c> only once nothing is left, so a crash at any point between them leaves the debt visible, and a
/// second lapse's later generation replaces an unfinished older one and covers its gate runs too.
/// </para>
/// </summary>
public static class QueueSweep
{
    /// <summary>The marker field naming the generation: merge groups whose gate ran before this time need it re-run.</summary>
    public const string Required = "sweep_required";

    /// <summary>The marker field naming the newest generation swept to its end.</summary>
    public const string Swept = "queue_swept";

    /// <summary>
    /// How far past <see cref="Required"/> a gate run may have started and still be re-run. It absorbs clock differences and
    /// the delay before a new lock becomes visible to a gate already running; re-running a gate that had already read the lock
    /// costs one gate run and changes nothing (ADR-016 point 2).
    /// </summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    /// <summary>How long a sweep may stay owed before the worker raises "queue sweep unfinished" (ADR-016 point 2).</summary>
    public static readonly TimeSpan UnfinishedAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The generation this lock still owes a sweep for, or null when it owes none: no generation was ever recorded, or
    /// <see cref="Swept"/> has caught up with the newest one.
    /// <para>
    /// A <see cref="Required"/> value that cannot be read names no generation, so it is no obligation: only Main Watcher writes
    /// these markers, a hand-edited one says nothing about which gate runs are suspect, and the next lock or lapse writes a
    /// fresh generation. Treating it as owed instead would owe a sweep that nothing could ever discharge.
    /// </para>
    /// </summary>
    public static DateTimeOffset? Owed(string? body)
    {
        if (Markers.Time(body, Required) is not { } required) return null;
        return Markers.Time(body, Swept) is { } swept && swept >= required ? null : required;
    }

    /// <summary>Gate runs that started before this are suspect: they may have decided before the lock was visible.</summary>
    public static DateTimeOffset Cutoff(DateTimeOffset required) => required + Margin;

    /// <summary>
    /// What to do about one gate run of a queued group. "Started before" is used rather than "completed before", because a gate
    /// reads the lock at some unknown point during its run: a run still going may already have read "no lock", so it is re-run
    /// once it completes, while one that started after the cut-off saw the lock and is left alone.
    /// <para>
    /// Only a run that <b>succeeded</b> is re-run. A gate that failed, was cancelled or was skipped is not what lets the group
    /// merge, and re-running it would only start the group's wait again.
    /// </para>
    /// </summary>
    public static GateRunHandling Handle(GateRun run, DateTimeOffset cutoff) =>
        run.StartedAt >= cutoff ? GateRunHandling.Leave
            : run.Status != "completed" ? GateRunHandling.Wait
            : run.Conclusion == "success" ? GateRunHandling.Rerun : GateRunHandling.Leave;
}
