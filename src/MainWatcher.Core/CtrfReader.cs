using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace MainWatcher.Core;

public sealed record FailedTest(string Name, string Suite, string Message);
/// <summary>One test's time in whole milliseconds, as CTRF gives it (ADR-007). <see cref="Retried"/> is set when it ran twice.</summary>
public sealed record TestTime(string Name, long DurationMs, bool Retried);
/// <summary>
/// A run's timing from its CTRF reports (ADR-011). <see cref="WallClockMs"/> spans the first attempt's report summaries and
/// <see cref="SummedMs"/> adds up the tests, which xUnit runs in parallel, so the two are kept apart. <see cref="Retried"/>
/// counts the tests that ran a second time, and <see cref="Slowest"/> holds this run's slowest tests, slowest first.
/// </summary>
public sealed record SuiteTiming(long WallClockMs, long SummedMs, int Retried, IReadOnlyList<TestTime> Slowest);
/// <summary>The failures of a set of reports; <see cref="Timing"/> is null when they are unknown.</summary>
public sealed record CtrfResult(bool Known, IReadOnlyList<FailedTest> Failures, SuiteTiming? Timing = null)
{
    public static CtrfResult Unknown { get; } = new(false, []);
}

/// <summary>Validates and merges project reports under the ADR-007 CTRF contract.</summary>
public static class CtrfReader
{
    public const int MaxReportBytes = 32 * 1024 * 1024;
    /// <summary>How many of a run's slowest tests the check run lists (ADR-011).</summary>
    public const int SlowestCount = 5;
    static readonly JsonSchema Schema = LoadSchema();
    static JsonSchema LoadSchema()
    {
        using var stream = typeof(CtrfReader).Assembly.GetManifestResourceStream("MainWatcher.Core.Schema.ctrf.schema.json")!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    public static CtrfResult Read(IEnumerable<string> reports)
    {
        var failures = new List<FailedTest>();
        var times = new List<TestTime>();
        long start = long.MaxValue, stop = long.MinValue;
        var count = 0;
        try
        {
            foreach (var report in reports)
            {
                count++;
                var json = JsonNode.Parse(report);
                if (!Schema.Evaluate(json).IsValid) return CtrfResult.Unknown;
                // CTRF times are milliseconds: epoch milliseconds for the summary, a count for each test. The schema makes all
                // of them integers, so no unit is guessed here (R-17).
                var summary = json!["results"]!["summary"]!;
                start = Math.Min(start, summary["start"]!.GetValue<long>());
                stop = Math.Max(stop, summary["stop"]!.GetValue<long>());
                foreach (var test in json["results"]!["tests"]!.AsArray())
                {
                    times.Add(new(test!["name"]!.GetValue<string>(), test["duration"]!.GetValue<long>(), test["retries"]?.GetValue<long>() > 0));
                    if (test!["status"]!.GetValue<string>() != "failed") continue;
                    var message = (test["message"]?.GetValue<string>() ?? "").Split(['\r', '\n'])[0];
                    if (message.Length > 200) message = message[..200];
                    failures.Add(new(test["name"]!.GetValue<string>(), test["suite"]?.GetValue<string>() ?? "unknown suite", message));
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException)
        {
            return CtrfResult.Unknown;
        }
        if (count == 0) return CtrfResult.Unknown;
        var slowest = times.OrderByDescending(t => t.DurationMs).ThenBy(t => t.Name, StringComparer.Ordinal).Take(SlowestCount).ToArray();
        return new(true, failures, new(Math.Max(0, stop - start), times.Sum(t => t.DurationMs), times.Count(t => t.Retried), slowest));
    }

    // Read entries directly, never extract target-controlled paths or execute target code.
    public static CtrfResult ReadZip(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = zip.Entries.Where(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !e.FullName.EndsWith("timings.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length > 1000 || entries.Sum(e => e.Length) > MaxReportBytes) return CtrfResult.Unknown;
            return Read(entries.Select(e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); }));
        }
        catch (InvalidDataException) { return CtrfResult.Unknown; }
    }
}
