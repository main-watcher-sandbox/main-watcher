namespace MainWatcher.Core;

/// <summary>ADR-017 scheduling rule shared by the Planner and trigger worker.</summary>
public static class Eligibility
{
    public static bool CanStart(string sha, IReadOnlyList<CheckRun> checks, TimeSpan interval,
        DateTimeOffset now, bool force = false)
    {
        if (checks.Any(c => c.Status != "completed")) return false;
        if (checks.Any(c => now - c.StartedAt < interval)) return false;
        var head = checks.Where(c => c.Sha == sha).OrderByDescending(c => c.StartedAt).ThenByDescending(c => c.Id).ToArray();
        if (head.Length == 0) return true;
        var latest = head[0];
        return latest.Conclusion == "neutral" && latest.CompletedAt is { } completed
            && now - completed >= interval && (force || head.Count(c => c.Conclusion == "neutral") < 3);
    }
}
