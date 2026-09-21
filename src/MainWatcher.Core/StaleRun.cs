namespace MainWatcher.Core;

/// <summary>How far a target run that has produced no result has got towards being stopped (ADR-013 point 5).</summary>
public enum StaleStage
{
    /// <summary>Within its deadlines, or completed: the outcome table judges it, not this rule.</summary>
    None,
    /// <summary>The job never started within the queue deadline.</summary>
    Queue,
    /// <summary>The job started and has run past its own timeout and the grace after it.</summary>
    Run,
    /// <summary>A stop was already asked for and the run has not stopped.</summary>
    Stopping
}

/// <summary>Where a stale run stands: its <see cref="Stage"/> and when that became true.</summary>
public sealed record StaleState(StaleStage Stage, DateTimeOffset Since);

/// <summary>
/// ADR-013 point 5: a target run that produces no result is stopped before it is judged. This is the one rule the Planner,
/// which does the stopping, and the trigger worker, which flags the run so a cycle runs at all, both read (TS-U5, TS-U15).
/// <para>
/// Waiting for a runner is not running, so there are two deadlines. Until the job starts, it is the check run's creation plus
/// <see cref="DefaultQueueDeadline"/>; once it has, the job's own <c>started_at</c> plus the time the reusable workflow gives
/// it. Every value comes from GitHub — the check run's creation time and output, and the job's status and times — so a crash
/// at any step is resumed on the next cycle.
/// </para>
/// </summary>
public static class StaleRun
{
    /// <summary>The job has not started this long after the check run was created. The sandbox shortens it (TS-S16 (g)).</summary>
    public static readonly TimeSpan DefaultQueueDeadline = TimeSpan.FromMinutes(30);

    /// <summary>How long after the job's own <c>timeout-minutes</c> the run deadline falls. GitHub normally ends the job first.</summary>
    public static readonly TimeSpan RunGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The margin <c>run-integration-tests.yml</c> adds to the target's <c>timeout</c> for the job's <c>timeout-minutes</c>.
    /// The jobs API does not report a job's timeout, so the run deadline is derived from the same two numbers the workflow
    /// uses. A target pinned to a tag with a different margin is covered by <see cref="RunGrace"/> on top.
    /// </summary>
    public static readonly TimeSpan JobMargin = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The check run output field holding the target's <c>timeout</c> as it was when the run was dispatched, in minutes.
    /// </summary>
    public const string TimeoutMinutes = "timeout_minutes";

    /// <summary>How long a cancel, and then a force-cancel, is given to stop the run before the next step.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromMinutes(15);

    /// <summary>The check run output field recording when the Planner asked GitHub to cancel the target run.</summary>
    public const string CancelRequested = "cancel_requested";

    /// <summary>The check run output field recording when the Planner asked GitHub to force-cancel it.</summary>
    public const string ForceCancelRequested = "force_cancel_requested";

    /// <summary>
    /// The queue deadline a setting asks for: whole minutes from 1 to 30, else <see cref="DefaultQueueDeadline"/>. Only the
    /// sandbox shortens it, and the watcher and the trigger worker must be given the same value, so that the worker starts a
    /// cycle for exactly the runs the Planner would then cancel.
    /// </summary>
    public static TimeSpan ConfiguredQueueDeadline(string? minutes) =>
        int.TryParse(minutes, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value is >= 1 and <= 30 ? TimeSpan.FromMinutes(value) : DefaultQueueDeadline;

    /// <summary>
    /// The queue deadline a sandbox setting gives <paramref name="repo"/>: its last entry for that target, or a bare one
    /// (<see cref="SandboxSwitch"/>), read as <see cref="ConfiguredQueueDeadline(string?)"/> reads a single value.
    /// </summary>
    public static TimeSpan ConfiguredQueueDeadline(string? setting, string repo) =>
        ConfiguredQueueDeadline(SandboxSwitch.For(setting, repo).LastOrDefault());

    /// <summary>
    /// Whether the job has left the queue. Judged on the status, not on <c>started_at</c>, which GitHub also fills in for a
    /// job that is still waiting for a runner. Any status other than these two is a state before the job runs.
    /// </summary>
    public static bool Started(WorkflowJob job) => job.Status is "in_progress" or "completed";

    /// <summary>The run's single <c>main-watcher</c> job; null when the run has none, or more than one (a contract error).</summary>
    public static WorkflowJob? TestJob(IReadOnlyList<WorkflowJob>? jobs) =>
        jobs?.Where(j => Outcomes.IsTestJob(j.Name)).ToArray() is [var job] ? job : null;

    /// <summary>
    /// The <c>timeout</c> the running job was dispatched with, from the check run's own output, falling back to the target's
    /// current setting.
    /// <para>
    /// ADR-013 counts the run deadline from the job's <b>own</b> <c>timeout-minutes</c>, which is fixed when GitHub creates
    /// the run. Reading <c>targets.yml</c> instead would move the deadline of a job already running: lowering a target's
    /// <c>timeout</c> from 120 to 30 would cancel a healthy job at 60 minutes rather than its real 150, and raising it would
    /// delay detection. So the Planner records the value with the check run and it is read back here. The fallback covers a
    /// check run created before this was recorded; its deadline is then only as stable as the configuration, as it was.
    /// </para>
    /// </summary>
    public static TimeSpan TestTimeout(CheckRun check, Target target) =>
        int.TryParse(Markers.Field(check.Summary, TimeoutMinutes), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var recorded) && recorded > 0
            ? TimeSpan.FromMinutes(recorded) : TimeSpan.FromMinutes(target.Timeout);

    /// <summary>
    /// When this run's deadline falls: the queue deadline while the job waits for a runner, the run deadline once it has one.
    /// Null for a completed job, and for a started job GitHub gives no <c>started_at</c> for, so an unreadable time never
    /// cancels a run that may still be testing.
    /// </summary>
    public static DateTimeOffset? Deadline(Target target, CheckRun check, WorkflowJob job, TimeSpan? queueDeadline = null)
    {
        if (job.Status == "completed") return null;
        if (!Started(job)) return check.StartedAt + (queueDeadline ?? DefaultQueueDeadline);
        return job.StartedAt is { } started ? started + TestTimeout(check, target) + JobMargin + RunGrace : null;
    }

    /// <summary>
    /// Where this check run's target run stands. The stage is <see cref="StaleStage.Stopping"/> from the moment a stop is
    /// recorded until the job completes, whatever the deadlines then say: a job that finally started after its queue deadline
    /// passed is still being cancelled, and must stay flagged until the run has stopped.
    /// </summary>
    public static StaleState State(Target target, CheckRun check, WorkflowJob job, DateTimeOffset now,
        TimeSpan? queueDeadline = null)
    {
        if (job.Status == "completed") return new(StaleStage.None, default);
        if (Markers.Time(check.Summary, CancelRequested) is { } asked) return new(StaleStage.Stopping, asked);
        if (Deadline(target, check, job, queueDeadline) is not { } deadline || now < deadline) return new(StaleStage.None, default);
        return new(Started(job) ? StaleStage.Run : StaleStage.Queue, deadline);
    }
}
