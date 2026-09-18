namespace MainWatcher.Core;

/// <summary>GitHub operations shared by the Planner, Reporter and trigger worker (ADR-009, ADR-012, ADR-017).</summary>
public interface IGitHubGateway
{
    Task ValidateTarget(Target target, CancellationToken ct);
    Task<string> MainHead(string repo, CancellationToken ct);
    Task<IReadOnlyList<CheckRun>> Checks(string repo, CancellationToken ct);
    /// <summary>Up to <paramref name="limit"/> commit SHAs reachable from <paramref name="sha"/>, newest first.</summary>
    Task<IReadOnlyList<string>> History(string repo, string sha, int limit, CancellationToken ct);
    /// <summary>This App's <c>main-watcher</c> check runs on one commit.</summary>
    Task<IReadOnlyList<CheckRun>> CommitChecks(string repo, string sha, CancellationToken ct);
    /// <summary>Up to <paramref name="limit"/> repository activity entries on <c>main</c>, newest first.</summary>
    Task<IReadOnlyList<Push>> Pushes(string repo, int limit, CancellationToken ct);
    /// <summary>Commits in <paramref name="after"/> that are not in <paramref name="before"/>; null when either commit cannot be compared.</summary>
    Task<int?> CommitCount(string repo, string before, string after, CancellationToken ct);
    /// <summary>
    /// Creates the <c>main-watcher</c> check run, in progress, with the given output. The output is written by the same call,
    /// so recording the run's settings costs no extra request.
    /// </summary>
    Task<CheckRun> CreateCheck(string repo, string sha, DateTimeOffset now, string title, string summary, CancellationToken ct);
    Task<long?> Dispatch(string repo, string sha, long checkId, CancellationToken ct);
    /// <summary>Starts <paramref name="workflow"/> on <c>main</c> with <paramref name="inputs"/>. Never retried.</summary>
    Task DispatchWorkflow(string repo, string workflow, IReadOnlyDictionary<string, string> inputs, CancellationToken ct);
    /// <summary>Dispatched runs of <paramref name="workflow"/> created at or after <paramref name="since"/>.</summary>
    Task<IReadOnlyList<WorkflowRun>> Runs(string repo, string workflow, DateTimeOffset since, CancellationToken ct);
    /// <summary>
    /// Merge groups whose gate failed open (ADR-008), from the gate runs created at or after <paramref name="since"/>; empty
    /// when the target has no such workflow.
    /// </summary>
    Task<IReadOnlyList<FailOpen>> FailOpens(string repo, DateTimeOffset since, CancellationToken ct);
    /// <summary>
    /// Merge groups the gate failed, from its runs created at or after <paramref name="since"/>; empty when the target has no
    /// such workflow. Each names the pull request its merge-queue branch carries, which is the entry the queue removed (R-7).
    /// </summary>
    Task<IReadOnlyList<GateBlock>> GateBlocks(string repo, DateTimeOffset since, CancellationToken ct);
    /// <summary>
    /// The commits <paramref name="after"/> added over <paramref name="before"/>, oldest first, each with the pull request its
    /// subject names (ADR-015). Null when GitHub cannot compare the two commits, which a force push can cause.
    /// </summary>
    Task<IReadOnlyList<MergedCommit>?> MergedCommits(string repo, string before, string after, CancellationToken ct);
    /// <summary>
    /// A pull request's <c>labeled</c>, <c>unlabeled</c> and <c>merged</c> events, oldest first (ADR-015 point 8). One read
    /// gives both the label history and the moment it is judged at.
    /// </summary>
    Task<IReadOnlyList<PullEvent>> PullEvents(string repo, int number, CancellationToken ct);
    Task Link(string repo, long checkId, long runId, CancellationToken ct);
    Task<IReadOnlyList<WorkflowJob>?> Jobs(string repo, long runId, CancellationToken ct);
    Task<CtrfResult> Reports(string repo, long runId, CancellationToken ct);
    /// <summary>
    /// Asks GitHub to stop a workflow run: <c>cancel</c>, or <c>force-cancel</c>, which skips the run's own cleanup
    /// (ADR-013 point 5). Both are idempotent, so a later cycle simply asks again.
    /// </summary>
    /// <returns>Null when GitHub accepted the request, else why it did not. Never throws: the escalation is the answer.</returns>
    Task<string?> CancelRun(string repo, long runId, bool force, CancellationToken ct);
    /// <summary>Replaces a check run's output without completing it, so it stays <c>in_progress</c>.</summary>
    Task Output(string repo, long checkId, string title, string summary, CancellationToken ct);
    Task Complete(string repo, long checkId, string conclusion, string title, string summary, CancellationToken ct);
    /// <summary>A file on <c>main</c>: null when it does not exist, "" when it is empty or too large to read inline.</summary>
    Task<string?> File(string repo, string path, CancellationToken ct);
    Task<IReadOnlyList<Issue>> OpenIssues(string repo, string label, CancellationToken ct);
    /// <summary>Issues with <paramref name="label"/> in any state, updated at or after <paramref name="since"/>.</summary>
    Task<IReadOnlyList<Issue>> Issues(string repo, string label, DateTimeOffset since, CancellationToken ct);
    /// <summary>An issue's comments, oldest first; only those updated at or after <paramref name="since"/> when it is given.</summary>
    Task<IReadOnlyList<IssueComment>> Comments(string repo, int number, DateTimeOffset? since, CancellationToken ct);
    /// <summary>Who closed an issue; null when it is open or GitHub does not say.</summary>
    Task<Account?> ClosedBy(string repo, int number, CancellationToken ct);
    Task EditBody(string repo, int number, string body, CancellationToken ct);
    /// <summary>Creates the label if it is missing, then an issue carrying it.</summary>
    Task<Issue> CreateIssue(string repo, string title, string body, string label, CancellationToken ct);
    Task Comment(string repo, int number, string body, CancellationToken ct);
    /// <summary>
    /// Closes an issue with a <c>state_reason</c> such as <c>completed</c> or <c>duplicate</c>. For <c>duplicate</c>,
    /// <paramref name="duplicateOf"/> is the canonical issue's database ID (<see cref="Issue.Id"/>).
    /// </summary>
    Task Close(string repo, int number, string reason, long? duplicateOf, CancellationToken ct);
}
