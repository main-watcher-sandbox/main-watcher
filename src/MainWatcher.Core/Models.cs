namespace MainWatcher.Core;

/// <summary>A check run. <see cref="Title"/> is its output title, such as "Infrastructure error"; null when it has none.</summary>
public sealed record CheckRun(long Id, string Sha, string Status, string? Conclusion,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? ExternalId, string? Title = null);
public sealed record WorkflowRun(long Id, string Title, DateTimeOffset CreatedAt, string Status);
public sealed record JobStep(string Name, string? Conclusion);
/// <summary>
/// A job of a workflow run. <see cref="CompletedAt"/> is null while it is queued or running, and is how long a report has been
/// pending once the check run's own <c>main-watcher</c> job has finished (ADR-013).
/// </summary>
public sealed record WorkflowJob(string Name, string Status, IReadOnlyList<JobStep> Steps, DateTimeOffset? CompletedAt = null);
/// <summary>
/// An issue. <see cref="StateReason"/> is GitHub's <c>state_reason</c>, such as <c>duplicate</c>; <see cref="UpdatedAt"/> is null
/// when unknown. <see cref="Id"/> is the database ID, which <c>duplicate_issue_id</c> takes, not the number.
/// </summary>
public sealed record Issue(int Number, string Title, string? Body, string Author, string AuthorType, string Url,
    string State = "open", string? StateReason = null, DateTimeOffset? UpdatedAt = null, long Id = 0);
public sealed record IssueComment(string Body, string Author, string AuthorType);
/// <summary>A GitHub account, with its <c>type</c> (<c>User</c> or <c>Bot</c>).</summary>
public sealed record Account(string Login, string Type);
/// <summary>One entry of the repository activity API on <c>main</c>. <see cref="Actor"/> is null for a deleted account.</summary>
public sealed record Push(string Before, string After, DateTimeOffset Timestamp, string Type, string? Actor);
