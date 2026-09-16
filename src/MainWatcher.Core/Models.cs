namespace MainWatcher.Core;

public sealed record CheckRun(long Id, string Sha, string Status, string? Conclusion,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? ExternalId);
public sealed record WorkflowRun(long Id, string Title, DateTimeOffset CreatedAt, string Status);
public sealed record JobStep(string Name, string? Conclusion);
public sealed record WorkflowJob(string Name, string Status, IReadOnlyList<JobStep> Steps);

/// <summary>The single scheduling rule used by the Planner and future trigger worker.</summary>
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

public interface IGitHubGateway
{
    Task<string> MainHead(string repo, CancellationToken ct);
    Task<IReadOnlyList<CheckRun>> Checks(string repo, CancellationToken ct);
    Task<CheckRun> CreateCheck(string repo, string sha, DateTimeOffset now, CancellationToken ct);
    Task<long?> Dispatch(string repo, string sha, long checkId, CancellationToken ct);
    Task<IReadOnlyList<WorkflowRun>> Runs(string repo, DateTimeOffset since, CancellationToken ct);
    Task Link(string repo, long checkId, long runId, CancellationToken ct);
    Task<IReadOnlyList<WorkflowJob>?> Jobs(string repo, long runId, CancellationToken ct);
    Task<CtrfResult> Reports(string repo, long runId, CancellationToken ct);
    Task Complete(string repo, long checkId, string conclusion, string summary, CancellationToken ct);
}
