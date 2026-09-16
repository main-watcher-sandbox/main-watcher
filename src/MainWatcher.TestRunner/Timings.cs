using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MainWatcher.TestRunner;

/// <summary>What the timings step knows about its run, apart from the CTRF reports.</summary>
public sealed record TimingsInput
{
    public required string Sha { get; init; }
    public long? RunId { get; init; }
    public int? RunAttempt { get; init; }

    /// <summary>When this attempt of the workflow run started, from the Actions API.</summary>
    public DateTimeOffset? RunStarted { get; init; }

    /// <summary>When the <c>main-watcher</c> job got a runner, from the Actions API.</summary>
    public DateTimeOffset? JobStarted { get; init; }

    public DateTimeOffset? RestoreStarted { get; init; }
    public DateTimeOffset? RestoreCompleted { get; init; }

    /// <summary>The <c>main-watcher-test</c> step: build, tests and the retry together.</summary>
    public DateTimeOffset? TestStarted { get; init; }

    public DateTimeOffset? TestCompleted { get; init; }

    /// <summary>The step's <c>retry-count</c> output was 1.</summary>
    public bool Retried { get; init; }
}

/// <summary>
/// Builds <c>timings.json</c> (ADR-011). All durations are whole milliseconds, and null when
/// unknown. The tests' wall-clock time and their summed time are kept apart, because xUnit runs
/// test collections, and <c>dotnet test</c> runs projects, in parallel.
/// </summary>
public static class Timings
{
    public const string FileName = "timings.json";
    public const int SchemaVersion = 1;

    public static JsonObject Build(TimingsInput input, IReadOnlyList<CtrfReport> reports)
    {
        var summaries = reports.Select(r => r.Json["results"]?["summary"]).OfType<JsonObject>().ToList();
        var starts = summaries.Select(s => Number(s["start"])).OfType<double>().ToList();
        var stops = summaries.Select(s => Number(s["stop"])).OfType<double>().ToList();
        var tests = reports.SelectMany(r => r.Tests.OfType<JsonObject>()).ToList();
        var durations = tests.Select(t => Number(t["duration"])).OfType<double>().ToList();

        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["sha"] = input.Sha,
            ["runId"] = input.RunId,
            ["runAttempt"] = input.RunAttempt,
            ["queueWaitMs"] = Between(input.RunStarted, input.JobStarted),
            ["steps"] = new JsonObject
            {
                ["restoreMs"] = Between(input.RestoreStarted, input.RestoreCompleted),
                ["testMs"] = Between(input.TestStarted, input.TestCompleted),
            },
            ["tests"] = new JsonObject
            {
                // From the first attempt's CTRF summaries: the retry is in steps.testMs only.
                ["wallClockMs"] = starts.Count > 0 && stops.Count > 0 ? (long)Math.Round(stops.Max() - starts.Min()) : null,
                ["summedMs"] = durations.Count > 0 ? (long)Math.Round(durations.Sum()) : null,
                ["count"] = tests.Count,
                ["reports"] = reports.Count,
            },
            ["retried"] = input.Retried || tests.Any(t => Number(t["retries"]) > 0),
        };
    }

    /// <summary>A Markdown table of the main figures, for the job summary.</summary>
    public static string Summary(JsonObject timings) =>
        "| Queue wait | Restore | Build and tests | Tests, wall clock | Tests, summed | Retried |\n" +
        "|---|---|---|---|---|---|\n" +
        $"| {Seconds(timings["queueWaitMs"])} | {Seconds(timings["steps"]?["restoreMs"])} | {Seconds(timings["steps"]?["testMs"])} " +
        $"| {Seconds(timings["tests"]?["wallClockMs"])} | {Seconds(timings["tests"]?["summedMs"])} | {(timings["retried"]?.GetValue<bool>() == true ? "yes" : "no")} |\n";

    static long? Between(DateTimeOffset? start, DateTimeOffset? stop) =>
        start is { } a && stop is { } b && b >= a ? (long)Math.Round((b - a).TotalMilliseconds) : null;

    // Parsed from text, so it works for nodes read from disk and nodes built in memory alike.
    static double? Number(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number ? double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture) : null;

    static string Seconds(JsonNode? milliseconds) =>
        Number(milliseconds) is { } ms ? $"{ms / 1000:0.0} s" : "unknown";
}
