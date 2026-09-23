using MainWatcher.Core;

namespace MainWatcher.Worker;

/// <summary>How far a <c>watch.yml</c> run that has not started has got towards being stopped (ADR-020).</summary>
public enum StuckStage
{
    /// <summary>Past the deadline: the worker cancels it.</summary>
    Cancel,
    /// <summary>A cancel has had <see cref="StuckWatchRuns.StopWait"/> and the run has not stopped: the worker force-cancels it.</summary>
    ForceCancel,
    /// <summary>A force-cancel has had its wait too. The worker force-cancels again on each cycle, and says so.</summary>
    Unstoppable,
    /// <summary>Held at an environment gate that lists reviewers. A person can approve it, so it is left alone.</summary>
    Reviewers
}

/// <summary>
/// A <c>watch.yml</c> run for <see cref="Repo"/> that has not started by its deadline. <see cref="Reviewers"/> is set only for
/// <see cref="StuckStage.Reviewers"/>.
/// </summary>
public sealed record StuckRun(string Repo, long RunId, string Url, string State, DateTimeOffset CreatedAt, DateTimeOffset Deadline,
    StuckStage Stage, IReadOnlyList<string> Reviewers);

/// <summary>
/// ADR-020: a <c>watch.yml</c> run for a target that has not started blocks every later cycle for that target, because the
/// worker skips a target whose run is queued or running (ADR-010). So a run still not <c>in_progress</c>
/// <see cref="Deadline"/> after it was created, plus any environment wait timer, is cancelled, then force-cancelled, as
/// ADR-013 point 5 stops a target run. Every time comes from GitHub, so a worker restart resumes where it was.
/// <para>
/// A run held for a listed reviewer is left alone: the <c>reporter</c> environment must have none, but cancelling a run a person
/// could approve would be wrong. A <c>sweep</c> run is out of scope, as it names no target.
/// </para>
/// </summary>
public static class StuckWatchRuns
{
    /// <summary>
    /// How long a run may go without starting. It clears the legitimate wait behind a sweep's job for the same target, whose
    /// timeout is 15 minutes.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(20);

    /// <summary>How long a cancel, and then a force-cancel, is given to stop the run, as for a target run (ADR-013 point 5).</summary>
    public static readonly TimeSpan StopWait = StaleRun.StopWait;

    /// <summary>
    /// Whether the run has not started: anything but <c>in_progress</c> and <c>completed</c>, such as <c>waiting</c> at an
    /// environment gate, or <c>queued</c>, <c>pending</c> or <c>requested</c>.
    /// </summary>
    public static bool Unstarted(WorkflowRun run) => run.Status is not ("in_progress" or "completed");

    /// <summary>
    /// The statuses a run has before it starts, which the worker lists whatever the run's age: a wait timer can put the
    /// deadline past <see cref="TriggerCycle.ActiveRunWindow"/>, and a run that cannot be stopped is force-cancelled until it
    /// stops (PR #72 review).
    /// </summary>
    public static readonly string[] UnstartedStatuses = ["waiting", "queued", "pending", "requested"];

    /// <summary>Whether the run is old enough for its gates to be worth reading. Nothing is read for a younger one.</summary>
    public static bool Due(WorkflowRun run, DateTimeOffset now) => Unstarted(run) && now - run.CreatedAt >= Deadline;

    /// <summary>
    /// Where a run stands, or null while it is within its deadline. <paramref name="gates"/> is what
    /// <c>pending_deployments</c> said for a <c>waiting</c> run, and empty for any other.
    /// </summary>
    public static StuckRun? Judge(string repo, string url, WorkflowRun run, IReadOnlyList<PendingDeployment> gates, DateTimeOffset now)
    {
        if (!Due(run, now)) return null;
        var reviewers = gates.SelectMany(g => g.Reviewers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (reviewers.Length > 0)
            return new(repo, run.Id, url, run.Status, run.CreatedAt, run.CreatedAt + Deadline, StuckStage.Reviewers, reviewers);
        // A wait timer is a legitimate hold, so it is added. GitHub counts it from when the job reached the gate, which is after
        // the run was created, so this can fall a little early for a long timer; the deadline's 20 minutes cover that.
        var deadline = run.CreatedAt + Deadline + gates.Select(g => g.WaitTimer).DefaultIfEmpty().Max();
        if (now < deadline) return null;
        var over = now - deadline;
        var stage = over < StopWait ? StuckStage.Cancel : over < 2 * StopWait ? StuckStage.ForceCancel : StuckStage.Unstoppable;
        return new(repo, run.Id, url, run.Status, run.CreatedAt, deadline, stage, []);
    }
}
