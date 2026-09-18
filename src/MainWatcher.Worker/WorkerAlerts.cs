using MainWatcher.Core;
using Microsoft.Extensions.Logging;

namespace MainWatcher.Worker;

/// <summary>A report the Reporter still owes: the check run is <c>in_progress</c> while its test job has finished (ADR-013).</summary>
/// <param name="Since">When the <c>main-watcher</c> job completed, or default when only this worker's clock can date it.</param>
public sealed record PendingReport(string Repo, long Check, string Reason, DateTimeOffset Since);

/// <summary>
/// A queue sweep a lock still owes: merge groups queued before it may yet merge onto a red <c>main</c> (ADR-016).
/// </summary>
/// <param name="Since">The generation owed, which is when the lock opened or its lapsed lease was renewed.</param>
public sealed record PendingSweep(string Repo, int Issue, string Reason, DateTimeOffset Since);

/// <summary>
/// What one cycle saw, beyond its counts. <see cref="WatchRunStarted"/> stays false and <see cref="WatchRunCompleted"/> null
/// when the run list could not be read, so an unreadable list never moves the "no run completed" clock either way.
/// </summary>
public sealed record CycleObservations
{
    /// <summary>What went wrong, one line each, for the alert body.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    public IReadOnlyList<PendingReport> Pending { get; init; } = [];
    /// <summary>Locks whose queue sweep is unfinished (ADR-016).</summary>
    public IReadOnlyList<PendingSweep> Sweeps { get; init; } = [];
    /// <summary>The enabled targets of this cycle. One that is gone from <c>targets.yml</c> is no longer owed anything.</summary>
    public IReadOnlyCollection<string> Targets { get; init; } = [];
    /// <summary>Targets whose work was looked at. A target skipped because its own cycle is running keeps its pending state.</summary>
    public IReadOnlyCollection<string> Examined { get; init; } = [];
    /// <summary>A <c>watch.yml</c> run was dispatched this cycle, or one is queued or running.</summary>
    public bool WatchRunStarted { get; init; }
    /// <summary>When the newest completed <c>watch.yml</c> run in <see cref="TriggerCycle.ActiveRunWindow"/> finished.</summary>
    public DateTimeOffset? WatchRunCompleted { get; init; }
}

/// <summary>
/// The worker's own health alerts, raised as de-duplicated <c>watcher-infra</c> issues in the watcher repo through
/// <c>mw-doorbell</c> (ADR-012, ADR-013). One <see cref="Review"/> per cycle judges every condition:
/// <list type="bullet">
///   <item>three cycle errors in a row;</item>
///   <item>no <c>watch.yml</c> run completed in 2 h, while runs were being started;</item>
///   <item>a credential GitHub refuses;</item>
///   <item>less than 20% of a rate-limit budget left (R-13);</item>
///   <item>a report pending for more than 15 minutes (ADR-013 point 6);</item>
///   <item>a queue sweep owed for more than 15 minutes (ADR-016 point 2).</item>
/// </list>
/// A condition that still holds raises nothing again until <see cref="Repeat"/> has passed, and <see cref="Core.Alerts"/> then
/// comments on the open issue rather than opening a second one (TS-U7). A condition that clears is forgotten, so its next
/// occurrence alerts at once. None of these is a required write: an alert that cannot be raised is logged and tried again.
/// </summary>
public sealed class WorkerAlerts(Alerts alerts, IAccessHealth access, ILogger log, DateTimeOffset started,
    Func<DateTimeOffset>? clock = null)
{
    /// <summary>Consecutive failing cycles before the worker alerts. A single failure is usually one flaky GitHub call.</summary>
    public const int ErrorsInARow = 3;

    /// <summary>How long the watcher may complete no <c>watch.yml</c> run while runs are being started (ADR-012).</summary>
    public static readonly TimeSpan NoRunWindow = TimeSpan.FromHours(2);

    /// <summary>How long a report may stay pending before it is made visible (ADR-013 point 6).</summary>
    public static readonly TimeSpan ReportPendingAfter = TimeSpan.FromMinutes(15);

    /// <summary>How long a queue sweep may stay owed before it is made visible (ADR-016 point 2).</summary>
    public static readonly TimeSpan SweepUnfinishedAfter = QueueSweep.UnfinishedAfter;

    /// <summary>A condition that still holds is repeated at most this often, so a lasting fault is one comment an hour.</summary>
    public static readonly TimeSpan Repeat = TimeSpan.FromHours(1);

    /// <summary>The share of a rate-limit budget below which the worker alerts (R-13).</summary>
    public const double RateLimitFloor = 0.2;

    readonly Dictionary<string, DateTimeOffset> firing = new(StringComparer.Ordinal);
    readonly Dictionary<string, OwedWork> reports = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, OwedWork> sweeps = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> streak = [];
    int consecutiveErrors;
    DateTimeOffset lastCompletedRun = started;
    DateTimeOffset lastStartedRun = DateTimeOffset.MinValue;

    /// <summary>Judges every condition after one cycle.</summary>
    /// <param name="observations">What the cycle saw; null when the cycle itself failed before it could say.</param>
    /// <param name="failure">Why the whole cycle failed, or null when it finished.</param>
    public async Task Review(CycleObservations? observations, string? failure, CancellationToken ct)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var seen = observations ?? new();
        var problems = failure is null ? seen.Problems : [failure, .. seen.Problems];
        consecutiveErrors = problems.Count > 0 ? consecutiveErrors + 1 : 0;
        // The alert names what went wrong across the whole run of failures, not only the cycle that crossed the threshold.
        if (problems.Count == 0) streak.Clear();
        else
        {
            streak.AddRange(problems.Select(p => $"{now.UtcDateTime:HH:mm} {p}"));
            if (streak.Count > 10) streak.RemoveRange(0, streak.Count - 10);
        }
        // The completion's own time, so reading the same finished run on every cycle does not push the clock forward with it.
        if (seen.WatchRunCompleted > lastCompletedRun) lastCompletedRun = seen.WatchRunCompleted.Value;
        if (seen.WatchRunStarted) lastStartedRun = now;

        await Judge(consecutiveErrors >= ErrorsInARow, "The trigger worker's cycles keep failing",
            $"The worker's last {consecutiveErrors} cycles failed. The most recent problems were:\n\n"
            + string.Join("\n", streak.Select(p => $"- {p}"))
            + "\n\nWhile its cycles fail, work is found only by the hourly sweep.", now, ct);

        // Only meaningful once runs are being started: an idle watcher legitimately completes none.
        await Judge(lastStartedRun > lastCompletedRun && now - lastCompletedRun >= NoRunWindow,
            "No `watch.yml` run has completed in 2 hours",
            "`watch.yml` runs have been started in the watcher repo, but none has completed since "
            + $"{lastCompletedRun.UtcDateTime:yyyy-MM-dd HH:mm} UTC. Check that Actions are enabled, that runners are free, "
            + "and that the runs are not failing before the Planner step.", now, ct);

        var refused = access.TokenFailures;
        await Judge(refused.Count > 0, "A GitHub App credential is being refused",
            "GitHub is refusing an installation token, so the worker cannot read or dispatch:\n\n"
            + string.Join("\n", refused.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"- {f.Key}: {f.Value}"))
            + "\n\nCheck the App's private key, and that it is still installed on that repository with the permissions it needs.",
            now, ct);

        // Only when GitHub reported a budget: a cycle that never reached the API says nothing about the limit.
        if (access.TakeLowestRateLimit() is { } limit)
            await Judge(limit.Left < RateLimitFloor, "GitHub rate limit below 20%",
                $"Only {limit.Remaining} of {limit.Limit} `{limit.Resource}` requests are left "
                + $"({limit.Left * 100:0}%{(limit.Reset is { } reset ? $", resetting at {reset.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "")}). "
                + "Reduce the number of targets or raise `check_period` (R-13).", now, ct);

        var ran = observations is not null;
        await Owed(seen.Pending.Select(p => new OwedWork(p.Repo, p.Reason, p.Since)).ToArray(), reports, seen, ran, now,
            ReportPendingAfter, repo => $"Reporting pending on {repo}",
            work => $"A report has been owed on `{work.Repo}` since {work.Since.UtcDateTime:yyyy-MM-dd HH:mm} UTC "
                + $"({(now - work.Since).TotalMinutes:0} minutes): {work.Reason}.\n\n"
                + "The Reporter writes the lock issue before it completes the check run, so an issue write that keeps failing "
                + "leaves the report owed, and no newer head of this target is tested meanwhile (ADR-013, R-20).", ct);

        await Owed(seen.Sweeps.Select(s => new OwedWork(s.Repo, s.Reason, s.Since)).ToArray(), sweeps, seen, ran, now,
            SweepUnfinishedAfter, repo => $"Queue sweep unfinished on {repo}",
            work => $"A queue sweep has been owed on `{work.Repo}` since {work.Since.UtcDateTime:yyyy-MM-dd HH:mm} UTC "
                + $"({(now - work.Since).TotalMinutes:0} minutes): {work.Reason}.\n\n"
                + "A merge group that passed the gate before the lock opened, or during a lease lapse, is re-checked by "
                + "re-running its gate (ADR-016). Until the sweep finishes, such a group can still merge onto a red `main`, "
                + "and it would be reported afterwards rather than blocked. The sweep stops for a gate run that is still "
                + "going, and for one GitHub refuses to re-run: the watcher's own run log says which.", ct);
    }

    /// <summary>One target's unpaid debt, as every one of these conditions is timed and worded.</summary>
    sealed record OwedWork(string Repo, string Reason, DateTimeOffset Since);

    /// <summary>
    /// Alerts for each target whose debt has been owed longer than <paramref name="after"/>, one condition per target so a
    /// second stuck target is not hidden by the first.
    /// <para>
    /// Every target still owing is judged, whether or not this cycle looked at it. A target whose own <c>watch.yml</c> run is
    /// queued or running is skipped by the cycle (ADR-010), so judging only what the cycle saw would hold the alert back
    /// exactly when the work is slowest: a watcher run that is itself waiting for a runner.
    /// </para>
    /// </summary>
    /// <param name="tracked">What this condition has seen owed, by target, so a debt survives the cycles that do not name it.</param>
    /// <param name="ran">Whether the cycle got far enough to name its targets. A cycle that threw says nothing about them.</param>
    async Task Owed(IReadOnlyList<OwedWork> found, Dictionary<string, OwedWork> tracked, CycleObservations seen, bool ran,
        DateTimeOffset now, TimeSpan after, Func<string, string> title, Func<OwedWork, string> body, CancellationToken ct)
    {
        foreach (var work in found)
            // The work's own time survives a restart; without one, the first cycle that saw it owed dates it.
            tracked[work.Repo] = work with
            {
                Since = work.Since != default ? work.Since
                    : tracked.TryGetValue(work.Repo, out var known) ? known.Since : now
            };
        // A target the cycle looked at and found owing nothing has paid, and one no longer configured owes nothing.
        // A target merely skipped for its own running cycle is neither, and keeps its clock.
        foreach (var gone in tracked.Keys.Except(found.Select(w => w.Repo), StringComparer.OrdinalIgnoreCase)
            .Where(repo => seen.Examined.Contains(repo, StringComparer.OrdinalIgnoreCase)
                || ran && !seen.Targets.Contains(repo, StringComparer.OrdinalIgnoreCase)).ToArray())
        {
            tracked.Remove(gone);
            await Judge(false, title(gone), "", now, ct);
        }
        foreach (var work in tracked.Values.ToArray())
            await Judge(now - work.Since >= after, title(work.Repo), body(work), now, ct);
    }

    /// <summary>Raises <paramref name="title"/> while <paramref name="holds"/>, at most once per <see cref="Repeat"/>.</summary>
    async Task Judge(bool holds, string title, string body, DateTimeOffset now, CancellationToken ct)
    {
        if (!holds)
        {
            if (firing.Remove(title)) log.LogInformation("Alert condition cleared: {Title}.", title);
            return;
        }
        if (firing.TryGetValue(title, out var raised) && now - raised < Repeat) return;
        try
        {
            await alerts.Raise(title, body, ct);
            firing[title] = now;
            log.LogWarning("Alert raised: {Title}.", title);
        }
        // Never a required write: the condition still holds next cycle, and it is often GitHub itself that is failing.
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogError("Alert \"{Title}\" could not be raised: {Message}", title, e.Message);
        }
    }
}
