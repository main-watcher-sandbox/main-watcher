namespace MainWatcher.Core;

/// <summary>GitHub operations shared by the Planner, Reporter and trigger worker (ADR-009, ADR-012, ADR-017).</summary>
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
    /// <summary>A file on <c>main</c>: null when it does not exist, "" when it is empty or too large to read inline.</summary>
    Task<string?> File(string repo, string path, CancellationToken ct);
    Task<IReadOnlyList<Issue>> OpenIssues(string repo, string label, CancellationToken ct);
    /// <summary>Creates the label if it is missing, then an issue carrying it.</summary>
    Task<Issue> CreateIssue(string repo, string title, string body, string label, CancellationToken ct);
    Task Comment(string repo, int number, string body, CancellationToken ct);
    Task Close(string repo, int number, CancellationToken ct);
}
