namespace MainWatcher.Core;

public sealed record CheckRun(long Id, string Sha, string Status, string? Conclusion,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? ExternalId);
public sealed record WorkflowRun(long Id, string Title, DateTimeOffset CreatedAt, string Status);
public sealed record JobStep(string Name, string? Conclusion);
public sealed record WorkflowJob(string Name, string Status, IReadOnlyList<JobStep> Steps);
public sealed record Issue(int Number, string Title, string? Body, string Author, string AuthorType, string Url);
/// <summary>One entry of the repository activity API on <c>main</c>. <see cref="Actor"/> is null for a deleted account.</summary>
public sealed record Push(string Before, string After, DateTimeOffset Timestamp, string Type, string? Actor);
