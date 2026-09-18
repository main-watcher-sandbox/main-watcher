using MainWatcher.Core;

namespace MainWatcher.Worker.Tests;

/// <summary>A faked gateway for one repository: a target, or the watcher repo seen by <c>mw-observer</c> or <c>mw-doorbell</c>.</summary>
sealed class FakeGitHub : IGitHubGateway
{
    public string Head { get; set; } = "head";
    public List<CheckRun> CheckList { get; set; } = [];
    public bool ChecksError { get; set; }
    /// <summary>Jobs by run ID; null for a deleted run. A run not listed has a <c>main-watcher</c> job still running.</summary>
    public Dictionary<long, IReadOnlyList<WorkflowJob>?> JobsByRun { get; } = [];
    public List<WorkflowRun> RunList { get; set; } = [];
    public bool RunsError { get; set; }
    public Dictionary<string, string> Files { get; } = [];
    public List<(string Repo, string Workflow, IReadOnlyDictionary<string, string> Inputs)> Dispatches { get; } = [];
    public List<string> Reads { get; } = [];
    /// <summary>Repository activity on main, newest first: how an eligible head's work is dated for the hourly sweep.</summary>
    public List<Push> Activity { get; } = [];
    /// <summary>The watcher repo's <c>watcher-infra</c> issues, for the worker's alerts.</summary>
    public List<Issue> IssueList { get; } = [];
    public Dictionary<int, List<string>> CommentsByIssue { get; } = [];
    public bool IssueWritesFail { get; set; }

    public Task<string> MainHead(string repo, CancellationToken ct) => Task.FromResult(Head);
    public Task<IReadOnlyList<CheckRun>> Checks(string repo, CancellationToken ct) =>
        ChecksError ? throw new HttpRequestException("checks unavailable") : Task.FromResult<IReadOnlyList<CheckRun>>(CheckList);
    public Task<IReadOnlyList<WorkflowJob>?> Jobs(string repo, long runId, CancellationToken ct) =>
        Task.FromResult(JobsByRun.TryGetValue(runId, out var jobs) ? jobs
            : [new("tests / main-watcher", "in_progress", [new("main-watcher-test", null)])]);
    public Task<IReadOnlyList<WorkflowRun>> Runs(string repo, string workflow, DateTimeOffset since, CancellationToken ct)
    {
        Reads.Add($"runs:{repo}:{workflow}");
        return RunsError ? throw new HttpRequestException("runs unavailable") : Task.FromResult<IReadOnlyList<WorkflowRun>>(RunList);
    }
    public Task<string?> File(string repo, string path, CancellationToken ct) => Task.FromResult(Files.GetValueOrDefault($"{repo}:{path}"));
    public Task DispatchWorkflow(string repo, string workflow, IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
    {
        Dispatches.Add((repo, workflow, inputs));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Issue>> OpenIssues(string repo, string label, CancellationToken ct) =>
        IssueWritesFail ? throw new HttpRequestException("issues unavailable")
            : Task.FromResult<IReadOnlyList<Issue>>(IssueList.Where(i => i.State == "open").ToArray());
    public Task<Issue> CreateIssue(string repo, string title, string body, string label, CancellationToken ct)
    {
        if (IssueWritesFail) throw new HttpRequestException("issues unavailable");
        var issue = new Issue(IssueList.Count + 1, title, body, "mw-doorbell[bot]", "Bot", $"https://github.com/{repo}/issues/{IssueList.Count + 1}");
        IssueList.Add(issue);
        return Task.FromResult(issue);
    }
    public Task Comment(string repo, int number, string body, CancellationToken ct)
    {
        if (IssueWritesFail) throw new HttpRequestException("issues unavailable");
        CommentsByIssue.TryAdd(number, []);
        CommentsByIssue[number].Add(body);
        return Task.CompletedTask;
    }

    // The worker only reads, and dispatches watch.yml and its own alerts.
    public Task ValidateTarget(Target target, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> History(string repo, string sha, int limit, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<CheckRun>> CommitChecks(string repo, string sha, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<Push>> Pushes(string repo, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Push>>(Activity.Take(limit).ToArray());
    public Task<int?> CommitCount(string repo, string before, string after, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<FailOpen>> FailOpens(string repo, DateTimeOffset since, CancellationToken ct) => throw new NotSupportedException();
    public Task<CheckRun> CreateCheck(string repo, string sha, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    public Task<long?> Dispatch(string repo, string sha, long checkId, CancellationToken ct) => throw new NotSupportedException();
    public Task Link(string repo, long checkId, long runId, CancellationToken ct) => throw new NotSupportedException();
    public Task<CtrfResult> Reports(string repo, long runId, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> CancelRun(string repo, long runId, bool force, CancellationToken ct) => throw new NotSupportedException();
    public Task Output(string repo, long checkId, string title, string summary, CancellationToken ct) => throw new NotSupportedException();
    public Task Complete(string repo, long checkId, string conclusion, string title, string summary, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<Issue>> Issues(string repo, string label, DateTimeOffset since, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<IssueComment>> Comments(string repo, int number, DateTimeOffset? since, CancellationToken ct) => throw new NotSupportedException();
    public Task<Account?> ClosedBy(string repo, int number, CancellationToken ct) => throw new NotSupportedException();
    public Task EditBody(string repo, int number, string body, CancellationToken ct) => throw new NotSupportedException();
    public Task Close(string repo, int number, string reason, long? duplicateOf, CancellationToken ct) => throw new NotSupportedException();
}
