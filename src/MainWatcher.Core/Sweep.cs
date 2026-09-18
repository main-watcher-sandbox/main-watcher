namespace MainWatcher.Core;

/// <summary>
/// What the hourly backup sweep does beyond an ordinary cycle (C-7, ADR-010). GitHub's scheduler is unreliable, so the sweep
/// is never the primary trigger; it does the work the trigger worker did not, and reports the two things only it looks for:
/// <list type="bullet">
///   <item>work that has waited longer than <see cref="WorkerDownAfter"/> with no cycle dispatched for it: the worker is down;</item>
///   <item><c>gate-fail-open</c> check runs of the past <see cref="FailOpenWindow"/> (ADR-008 point 3).</item>
/// </list>
/// Neither alert is a required write: the caller reports a failure and fails the run, and the next sweep judges again. The
/// cycle's own work is never held back by one.
/// </summary>
/// <param name="github">The target's gateway, holding the <c>main-watcher</c> App token scoped to it.</param>
/// <param name="watcher">The watcher repo's own gateway, which reads this workflow's runs.</param>
public sealed class Sweep(IGitHubGateway github, IGitHubGateway watcher, string watcherRepo, Alerts alerts,
    WorkFinder finder, Func<DateTimeOffset>? clock = null)
{
    /// <summary>How long work may wait for the trigger worker, which looks every minute, before it is presumed down (ADR-010).</summary>
    public static readonly TimeSpan WorkerDownAfter = TimeSpan.FromMinutes(15);

    /// <summary>How far back a sweep looks for merge groups whose gate failed open. The sweep itself runs hourly (ADR-008).</summary>
    public static readonly TimeSpan FailOpenWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Raises "trigger worker appears down" when this target's work has waited longer than <see cref="WorkerDownAfter"/>.
    /// Call it before the cycle, which is what does that work.
    /// <para>
    /// Work whose age GitHub does not date is never reported, and neither is work on a target the worker has dispatched a cycle
    /// for within that time: a Reporter that cannot finish leaves work waiting while the worker is perfectly alive, and the
    /// worker raises its own "reporting pending" alert for that (ADR-013).
    /// </para>
    /// </summary>
    /// <returns>The waiting work, or null when there is nothing to report.</returns>
    public async Task<Work?> WorkerDown(Target target, CancellationToken ct)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        if (await finder.Find(target, github, ct) is not { Since: { } since } work || now - since < WorkerDownAfter) return null;
        var dispatched = await Dispatched(target.Repo, now - WorkerDownAfter, ct);
        if (dispatched == true) return null;
        await alerts.Raise($"Trigger worker appears down (work waiting on {target.Repo})",
            $"The hourly sweep found work on `{target.Repo}` that has waited "
            + $"{(now - since).TotalMinutes:0} minutes, since {since.UtcDateTime:yyyy-MM-dd HH:mm} UTC: {work.Reason}.\n\n"
            + (dispatched is null
                ? $"Whether `{GitHubGateway.WatchWorkflow}` cycles were dispatched for it could not be read."
                : $"No `{GitHubGateway.WatchWorkflow}` cycle has been dispatched for it in that time.")
            + " The trigger worker starts one within a minute of work appearing (ADR-010), so check its pod, its `mw-doorbell`"
            + " credential and its own alerts. This sweep is doing the work meanwhile, so `main` is watched hourly, not"
            + " continuously (C-7).", ct);
        return work;
    }

    /// <summary>
    /// Whether a <c>watch.yml</c> cycle for <paramref name="repo"/> was dispatched at or after <paramref name="since"/>; null
    /// when the run list could not be read, which suppresses nothing: the waiting work is the signal that matters.
    /// </summary>
    async Task<bool?> Dispatched(string repo, DateTimeOffset since, CancellationToken ct)
    {
        try
        {
            return (await watcher.Runs(watcherRepo, GitHubGateway.WatchWorkflow, since, ct))
                .Any(r => r.Title.Equals(GitHubGateway.WatchRunPrefix + repo, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is HttpRequestException or IOException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>
    /// Raises one alert for the merge groups whose gate failed open in the past <see cref="FailOpenWindow"/> (ADR-008 point 3).
    /// This is a secondary signal: the check run may never have been posted, since a gate that cannot reach the API usually
    /// cannot report through it either. Merges made while a lock was open are reported by reconciliation instead (ADR-015).
    /// </summary>
    /// <returns>How many fail-opens were reported.</returns>
    public async Task<int> GateFailedOpen(Target target, CancellationToken ct)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var found = await github.FailOpens(target.Repo, now - FailOpenWindow, ct);
        if (found.Count == 0) return 0;
        // Naming the runs keeps a second sweep within the same hour from reporting the same set again (ADR-012).
        var key = $"<!-- main-watcher gate-fail-open runs={string.Join(",", found.Select(f => f.RunId).Order())} -->";
        await alerts.Raise($"Gate failed open on {target.Repo}",
            $"The gate let {found.Count} merge group(s) through on `{target.Repo}` without being able to enforce"
            + $" a lock, in the hour to {now.UtcDateTime:yyyy-MM-dd HH:mm} UTC:\n\n"
            + string.Join("\n", found.Select(f =>
                $"- {f.At.UtcDateTime:yyyy-MM-dd HH:mm} UTC, [run {f.RunId}](https://github.com/{target.Repo}/actions/runs/{f.RunId})"
                + $" on `{Markdown.Escape(f.Branch)}` ({Short(f.Sha)})"))
            + "\n\nEach run's job summary gives the reason: the lock state could not be read after three retries, or its lease"
            + " had expired (ADR-008, ADR-014). Merges made while a lock was open are reported separately by reconciliation.", ct, key);
        return found.Count;
    }

    static string Short(string sha) => Markdown.Short(sha);
}
