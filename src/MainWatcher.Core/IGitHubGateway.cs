namespace MainWatcher.Core;

public interface IGitHubGateway
{
    Task ValidateTarget(Target target, CancellationToken ct);
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
