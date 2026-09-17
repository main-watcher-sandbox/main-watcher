using MainWatcher.Core;
using Microsoft.Extensions.Logging;

namespace MainWatcher.Worker;

/// <summary>A report the Reporter still owes: the check run is <c>in_progress</c> while its test job has finished (ADR-013).</summary>
/// <param name="Since">When the <c>main-watcher</c> job completed, or default when only this worker's clock can date it.</param>
public sealed record PendingReport(string Repo, long Check, string Reason, DateTimeOffset Since);

/// <summary>
/// What one cycle saw, beyond its counts. <see cref="WatchRunStarted"/> stays false and <see cref="WatchRunCompleted"/> null
/// when the run list could not be read, so an unreadable list never moves the "no run completed" clock either way.
/// </summary>
public sealed record CycleObservations
{
    /// <summary>What went wrong, one line each, for the alert body.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    public IReadOnlyList<PendingReport> Pending { get; init; } = [];
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
///   <item>a report pending for more than 15 minutes (ADR-013 point 6).</item>
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

    /// <summary>A condition that still holds is repeated at most this often, so a lasting fault is one comment an hour.</summary>
    public static readonly TimeSpan Repeat = TimeSpan.FromHours(1);

    /// <summary>The share of a rate-limit budget below which the worker alerts (R-13).</summary>
    public const double RateLimitFloor = 0.2;

    readonly Dictionary<string, DateTimeOffset> firing = new(StringComparer.Ordinal);
    readonly Dictionary<string, PendingReport> owed = new(StringComparer.OrdinalIgnoreCase);
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

        await Pending(seen, observations is not null, now, ct);
    }

    /// <summary>
    /// Alerts for each report pending longer than <see cref="ReportPendingAfter"/>, one condition per target so a second stuck
    /// target is not hidden by the first.
    /// <para>
    /// Every target still owing a report is judged, whether or not this cycle looked at it. A target whose own
    /// <c>watch.yml</c> run is queued or running is skipped by the cycle (ADR-010), so judging only what the cycle saw would
    /// hold the alert back exactly when reporting is slowest: a Reporter that is itself waiting for a runner.
    /// </para>
    /// </summary>
    /// <param name="ran">Whether the cycle got far enough to name its targets. A cycle that threw says nothing about them.</param>
    async Task Pending(CycleObservations seen, bool ran, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var report in seen.Pending)
            // The job's own completion time survives a restart; without one, the first cycle that saw the report owed dates it.
            owed[report.Repo] = report with
            {
                Since = report.Since != default ? report.Since
                    : owed.TryGetValue(report.Repo, out var known) ? known.Since : now
            };
        // A target the cycle looked at and found owing nothing has been reported, and one no longer configured owes nothing.
        // A target merely skipped for its own running cycle is neither, and keeps its clock.
        foreach (var gone in owed.Keys.Except(seen.Pending.Select(p => p.Repo), StringComparer.OrdinalIgnoreCase)
            .Where(repo => seen.Examined.Contains(repo, StringComparer.OrdinalIgnoreCase)
                || ran && !seen.Targets.Contains(repo, StringComparer.OrdinalIgnoreCase)).ToArray())
        {
            owed.Remove(gone);
            await Judge(false, PendingTitle(gone), "", now, ct);
        }
        foreach (var report in owed.Values.ToArray())
            await Judge(now - report.Since >= ReportPendingAfter, PendingTitle(report.Repo),
                $"A report has been owed on `{report.Repo}` since {report.Since.UtcDateTime:yyyy-MM-dd HH:mm} UTC "
                + $"({(now - report.Since).TotalMinutes:0} minutes): {report.Reason}.\n\n"
                + "The Reporter writes the lock issue before it completes the check run, so an issue write that keeps failing "
                + "leaves the report owed, and no newer head of this target is tested meanwhile (ADR-013, R-20).", now, ct);
    }

    static string PendingTitle(string repo) => $"Reporting pending on {repo}";

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
