namespace MainWatcher.Core;

/// <summary>
/// A check run. <see cref="Title"/> is its output title, such as "Infrastructure error", and <see cref="Summary"/> its output
/// summary, which carries the stale-run markers (ADR-013 point 5); both are null when it has no output.
/// </summary>
public sealed record CheckRun(long Id, string Sha, string Status, string? Conclusion,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? ExternalId, string? Title = null, string? Summary = null);
/// <summary>
/// A workflow run. <see cref="UpdatedAt"/> is when GitHub last changed it, which for a completed run is when it finished;
/// null when GitHub did not say.
/// </summary>
public sealed record WorkflowRun(long Id, string Title, DateTimeOffset CreatedAt, string Status, DateTimeOffset? UpdatedAt = null);
public sealed record JobStep(string Name, string? Conclusion);
/// <summary>
/// A job of a workflow run. <see cref="CompletedAt"/> is null while it is queued or running, and is how long a report has been
/// pending once the check run's own <c>main-watcher</c> job has finished (ADR-013). <see cref="StartedAt"/> dates the run
/// deadline; GitHub fills it in for a queued job too, so <see cref="StaleRun.Started"/> reads the status instead.
/// </summary>
public sealed record WorkflowJob(string Name, string Status, IReadOnlyList<JobStep> Steps, DateTimeOffset? CompletedAt = null,
    DateTimeOffset? StartedAt = null);
/// <summary>
/// An issue. <see cref="StateReason"/> is GitHub's <c>state_reason</c>, such as <c>duplicate</c>; <see cref="UpdatedAt"/> is null
/// when unknown. <see cref="Id"/> is the database ID, which <c>duplicate_issue_id</c> takes, not the number.
/// <see cref="CreatedAt"/> and <see cref="ClosedAt"/> bound a lock's window (ADR-015); <see cref="ClosedAt"/> is null while it
/// is open, and both are null when GitHub does not say.
/// </summary>
public sealed record Issue(int Number, string Title, string? Body, string Author, string AuthorType, string Url,
    string State = "open", string? StateReason = null, DateTimeOffset? UpdatedAt = null, long Id = 0,
    DateTimeOffset? CreatedAt = null, DateTimeOffset? ClosedAt = null);
public sealed record IssueComment(string Body, string Author, string AuthorType);
/// <summary>A GitHub account, with its <c>type</c> (<c>User</c> or <c>Bot</c>).</summary>
public sealed record Account(string Login, string Type);
/// <summary>One entry of the repository activity API on <c>main</c>. <see cref="Actor"/> is null for a deleted account.</summary>
public sealed record Push(string Before, string After, DateTimeOffset Timestamp, string Type, string? Actor);
/// <summary>
/// A merge group whose gate passed without being able to enforce a lock (ADR-008, ADR-014): the gate run that recorded it with
/// a <c>main-watcher/gate-fail-open</c> check run. <see cref="Branch"/> is the merge-queue branch it ran on.
/// </summary>
public sealed record FailOpen(long RunId, string Sha, string Branch, DateTimeOffset At);
/// <summary>
/// One commit an activity entry put on <c>main</c> (ADR-015). <see cref="Pull"/> is the pull request its subject names, or
/// null when it names none, and <see cref="At"/> is when it was committed, which is when its pull request merged.
/// </summary>
public sealed record MergedCommit(string Sha, string Subject, DateTimeOffset At, int? Pull);
/// <summary>
/// One <c>labeled</c> or <c>unlabeled</c> event on a pull request, from the Issues events API.
/// <see cref="Added"/> is true for <c>labeled</c>.
/// </summary>
public sealed record LabelEvent(string Label, bool Added, DateTimeOffset At);
/// <summary>
/// A merge group the gate failed while a lock was open (ADR-002, R-7): the gate run that failed it, and the pull request its
/// merge-queue branch names, which is the entry the queue removed.
/// </summary>
public sealed record GateBlock(long RunId, int Pull, string Branch, DateTimeOffset At);
