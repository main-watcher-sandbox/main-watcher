namespace MainWatcher.Core;

/// <summary>ADR-017 scheduling rule shared by the Planner and trigger worker.</summary>
public static class Eligibility
{
    /// <summary>How many neutral results one head may collect before it stops being tested (ADR-017).</summary>
    public const int Cap = 3;

    public static bool CanStart(string sha, IReadOnlyList<CheckRun> checks, TimeSpan interval,
        DateTimeOffset now, bool force = false)
    {
        if (checks.Any(c => c.Status != "completed")) return false;
        if (checks.Any(c => now - c.StartedAt < interval)) return false;
        var head = Head(sha, checks);
        if (head.Length == 0) return true;
        var latest = head[0];
        return latest.Conclusion == "neutral" && latest.CompletedAt is { } completed
            && now - completed >= interval && (force || head.Count(c => c.Conclusion == "neutral") < Cap);
    }

    /// <summary>
    /// Whether the head has spent its attempts: its newest check run is neutral and it has <see cref="Cap"/> of them. This is the
    /// one reason <see cref="CanStart"/> refuses a head that no later cycle can lift, so it is what the Planner alerts on. It is
    /// true from the moment the capping neutral is written, before the wait that would otherwise make the head eligible again.
    /// </summary>
    public static bool Capped(string sha, IReadOnlyList<CheckRun> checks)
    {
        var head = Head(sha, checks);
        return head.FirstOrDefault()?.Conclusion == "neutral" && head.Count(c => c.Conclusion == "neutral") >= Cap;
    }

    /// <summary>The head's own check runs, newest first; ties broken by ID, since two can share a start time.</summary>
    static CheckRun[] Head(string sha, IReadOnlyList<CheckRun> checks) =>
        checks.Where(c => c.Sha == sha).OrderByDescending(c => c.StartedAt).ThenByDescending(c => c.Id).ToArray();
}
