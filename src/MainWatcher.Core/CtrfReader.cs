using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace MainWatcher.Core;

public sealed record FailedTest(string Name, string Suite, string Message);
public sealed record CtrfResult(bool Known, IReadOnlyList<FailedTest> Failures)
{
    public static CtrfResult Unknown { get; } = new(false, []);
}

public static class CtrfReader
{
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
        var count = 0;
        try
        {
            foreach (var report in reports)
            {
                count++;
                var json = JsonNode.Parse(report);
                if (!Schema.Evaluate(json).IsValid) return CtrfResult.Unknown;
                foreach (var test in json!["results"]!["tests"]!.AsArray())
                {
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
        return count == 0 ? CtrfResult.Unknown : new(true, failures);
    }

    // Read entries directly, never extract target-controlled paths or execute target code.
    public static CtrfResult ReadZip(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = zip.Entries.Where(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !e.FullName.EndsWith("timings.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length > 1000 || entries.Sum(e => e.Length) > 32 * 1024 * 1024) return CtrfResult.Unknown;
            return Read(entries.Select(e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); }));
        }
        catch (InvalidDataException) { return CtrfResult.Unknown; }
    }
}
