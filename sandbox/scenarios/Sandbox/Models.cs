using System.Text.Json.Nodes;

namespace MainWatcher.Scenarios.Sandbox;

/// <summary>A <c>main-watcher</c> check run, as the watcher leaves it on a target commit.</summary>
public sealed record CheckRun(long Id, string HeadSha, string Status, string? Conclusion, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, string? ExternalId, string Title, string Summary)
{
    public bool Completed => Status == "completed";

    /// <summary>The target run the check run belongs to, once the Planner has linked it.</summary>
    public long? RunId => long.TryParse(ExternalId, out var id) ? id : null;

    public static CheckRun From(JsonNode node) => new(
        node["id"]!.GetValue<long>(),
        node["head_sha"]!.GetValue<string>(),
        node["status"]!.GetValue<string>(),
        node["conclusion"]?.GetValue<string>(),
        Time(node["started_at"]) ?? DateTimeOffset.MinValue,
        Time(node["completed_at"]),
        node["external_id"]?.GetValue<string>(),
        node["output"]?["title"]?.GetValue<string>() ?? "",
        node["output"]?["summary"]?.GetValue<string>() ?? "");

    public override string ToString() => $"check {Id} ({Status}{(Conclusion is null ? "" : $", {Conclusion}")}, \"{Title}\")";

    internal static DateTimeOffset? Time(JsonNode? node) =>
        node?.GetValue<string>() is { Length: > 0 } text ? DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : null;
}

/// <summary>An issue: a lock on a target, or a <c>watcher-infra</c> alert on the replica.</summary>
public sealed record Issue(int Number, string Title, string Body, string State, string Author, DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt, string? ClosedBy, string? StateReason, string[] Labels, string Url)
{
    public static Issue From(JsonNode node) => new(
        node["number"]!.GetValue<int>(),
        node["title"]!.GetValue<string>(),
        node["body"]?.GetValue<string>() ?? "",
        node["state"]!.GetValue<string>(),
        node["user"]!["login"]!.GetValue<string>(),
        CheckRun.Time(node["created_at"])!.Value,
        CheckRun.Time(node["closed_at"]),
        node["closed_by"]?["login"]?.GetValue<string>(),
        node["state_reason"]?.GetValue<string>(),
        (node["labels"] as JsonArray ?? []).Select(l => l!["name"]!.GetValue<string>()).ToArray(),
        node["html_url"]!.GetValue<string>());

    public override string ToString() => $"#{Number} \"{Title}\" ({State})";
}

/// <summary>A comment on an issue.</summary>
public sealed record Comment(long Id, string Author, string Body, DateTimeOffset CreatedAt)
{
    public static Comment From(JsonNode node) => new(
        node["id"]!.GetValue<long>(),
        node["user"]!["login"]!.GetValue<string>(),
        node["body"]?.GetValue<string>() ?? "",
        CheckRun.Time(node["created_at"])!.Value);
}

/// <summary>A workflow run, on a target or on the replica.</summary>
public sealed record Run(long Id, string Title, string Event, string Status, string? Conclusion, string HeadBranch, string HeadSha,
    DateTimeOffset CreatedAt, int Attempt)
{
    public bool Completed => Status == "completed";

    public static Run From(JsonNode node) => new(
        node["id"]!.GetValue<long>(),
        node["display_title"]?.GetValue<string>() ?? "",
        node["event"]!.GetValue<string>(),
        node["status"]!.GetValue<string>(),
        node["conclusion"]?.GetValue<string>(),
        node["head_branch"]?.GetValue<string>() ?? "",
        node["head_sha"]!.GetValue<string>(),
        CheckRun.Time(node["created_at"])!.Value,
        node["run_attempt"]?.GetValue<int>() ?? 1);

    public override string ToString() => $"run {Id} \"{Title}\" ({Status}{(Conclusion is null ? "" : $", {Conclusion}")})";
}

/// <summary>A step of a job.</summary>
public sealed record Step(string Name, string Status, string? Conclusion);

/// <summary>A job of a workflow run, with its steps.</summary>
public sealed record Job(long Id, string Name, string Status, string? Conclusion, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    Step[] Steps)
{
    public bool Completed => Status == "completed";

    /// <summary>A job has left the queue once it is running or done; GitHub fills in <c>started_at</c> for queued jobs too.</summary>
    public bool Started => Status is "in_progress" or "completed";

    public Step? StepNamed(string name) => Steps.SingleOrDefault(s => s.Name == name);

    public static Job From(JsonNode node) => new(
        node["id"]!.GetValue<long>(),
        node["name"]!.GetValue<string>(),
        node["status"]!.GetValue<string>(),
        node["conclusion"]?.GetValue<string>(),
        CheckRun.Time(node["started_at"]),
        CheckRun.Time(node["completed_at"]),
        (node["steps"] as JsonArray ?? []).Select(s => new Step(
            s!["name"]!.GetValue<string>(), s["status"]!.GetValue<string>(), s["conclusion"]?.GetValue<string>())).ToArray());

    public override string ToString() =>
        $"job \"{Name}\" ({Status}{(Conclusion is null ? "" : $", {Conclusion}")}): " +
        string.Join(", ", Steps.Select(s => $"{s.Name}={s.Conclusion ?? s.Status}"));
}

/// <summary>A pull request the suite opened on a target.</summary>
public sealed record PullRequest(int Number, string NodeId, string Branch, string HeadSha, string Title);
