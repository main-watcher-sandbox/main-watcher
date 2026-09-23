using System.Text.Json.Nodes;

namespace MainWatcher.Scenarios.Infra;

/// <summary>
/// Keeps the raw jobs responses of every target test run the suite reads, under <c>jobs/</c> in the run's output folder, so a
/// surprise in how GitHub reports a job comes with its evidence (#65). For each run it keeps two files:
/// <list type="bullet">
///   <item><c>&lt;run&gt;-first-completed.json</c>, the first response in which the <c>main-watcher</c> job had completed,
///   never overwritten: the moment the Reporter may read a job whose steps GitHub has not finished writing down;</item>
///   <item><c>&lt;run&gt;-latest.json</c>, the newest response, overwritten on every read.</item>
/// </list>
/// </summary>
public static class JobsLog
{
    static readonly Lock gate = new();

    /// <summary>The run's output folder; nothing is saved while it is empty.</summary>
    public static string OutDir { get; set; } = "";

    public static bool IsTestJob(JsonNode job) =>
        job["name"]?.GetValue<string>() is { } name && (name == "main-watcher" || name.EndsWith("/ main-watcher", StringComparison.Ordinal));

    public static void Save(string repo, long runId, int? attempt, IReadOnlyList<JsonNode> jobs)
    {
        if (OutDir.Length == 0 || jobs.FirstOrDefault(IsTestJob) is not { } test) return;
        var folder = Path.Combine(OutDir, "jobs", repo[(repo.IndexOf('/') + 1)..]);
        var stem = Path.Combine(folder, attempt is null ? $"{runId}" : $"{runId}-a{attempt}");
        var text = Snapshot(jobs).ToJsonString(new() { WriteIndented = true });
        lock (gate)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText($"{stem}-latest.json", text);
            if (test["status"]?.GetValue<string>() == "completed" && !File.Exists($"{stem}-first-completed.json"))
                File.WriteAllText($"{stem}-first-completed.json", text);
        }
    }

    /// <summary>The jobs as GitHub gave them, with the time they were read.</summary>
    public static JsonObject Snapshot(IReadOnlyList<JsonNode> jobs) => new()
    {
        ["read_at"] = DateTimeOffset.UtcNow.ToString("O"),
        ["jobs"] = new JsonArray(jobs.Select(j => j.DeepClone()).ToArray())
    };
}
