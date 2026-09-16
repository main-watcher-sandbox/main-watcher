using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MainWatcher.TestRunner;

/// <summary>One CTRF report file, as read from disk.</summary>
public sealed record CtrfReport(string Path, JsonObject Json)
{
    public JsonArray Tests => Json["results"]?["tests"] as JsonArray ?? [];
}

/// <summary>
/// Reads the CTRF reports a test command wrote (ADR-007), picks the failed tests to retry, and
/// folds the retry's results back into the first attempt's reports.
/// </summary>
public static class CtrfReports
{
    public const string DefaultGlob = "**/TestResults/*.ctrf.json";

    /// <summary>Reports under <paramref name="root"/> matching <paramref name="glob"/>. Unreadable files are skipped.</summary>
    public static IReadOnlyList<CtrfReport> Load(string root, string glob, Action<string> log)
    {
        var pattern = GlobToRegex(glob);
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(path => pattern.IsMatch(Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Order(StringComparer.Ordinal);

        var reports = new List<CtrfReport>();
        foreach (var path in files)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject json)
                    reports.Add(new CtrfReport(path, json));
                else
                    log($"::warning::{path} is not a CTRF report; ignored");
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                log($"::warning::{path} could not be read ({e.Message}); ignored");
            }
        }

        return reports;
    }

    /// <summary>The <c>--filter-method</c> names of the failed tests, without duplicates.</summary>
    public static IReadOnlyList<string> FailedTestNames(IEnumerable<CtrfReport> reports) =>
        reports.SelectMany(r => r.Tests.OfType<JsonObject>())
            .Where(test => Status(test) == "failed")
            .Select(FilterName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The fully qualified method name. xUnit v3 puts it in <c>extra.type</c> and
    /// <c>extra.method</c>; otherwise the test name without theory arguments is used.
    /// </summary>
    public static string FilterName(JsonObject test)
    {
        if (test["extra"]?["type"]?.GetValueKind() == JsonValueKind.String
            && test["extra"]?["method"]?.GetValueKind() == JsonValueKind.String)
            return $"{test["extra"]!["type"]!.GetValue<string>()}.{test["extra"]!["method"]!.GetValue<string>()}";

        var name = test["name"]?.GetValueKind() == JsonValueKind.String ? test["name"]!.GetValue<string>() : "";
        var arguments = name.IndexOf('(');
        return (arguments < 0 ? name : name[..arguments]).Trim();
    }

    /// <summary>
    /// Rewrites every first-attempt report. The retry overwrote them, and left projects with
    /// no retried tests empty. Each retried test takes its retry result, with <c>retries</c>
    /// set to 1 and <c>flaky</c> set when it passed; one missing from the retry stays failed.
    /// </summary>
    public static void MergeRetry(IReadOnlyList<CtrfReport> firstAttempt, IReadOnlyList<CtrfReport> retry, IReadOnlyCollection<string> retriedNames)
    {
        var retried = new HashSet<string>(retriedNames, StringComparer.Ordinal);
        var retryResults = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var test in retry.SelectMany(r => r.Tests.OfType<JsonObject>()))
        {
            if (test["name"]?.GetValueKind() == JsonValueKind.String)
                retryResults[test["name"]!.GetValue<string>()] = test;
        }

        foreach (var report in firstAttempt)
        {
            var tests = report.Tests;
            for (var i = 0; i < tests.Count; i++)
            {
                if (tests[i] is not JsonObject test || Status(test) != "failed" || !retried.Contains(FilterName(test)))
                    continue;

                var name = test["name"]?.GetValueKind() == JsonValueKind.String ? test["name"]!.GetValue<string>() : null;
                var merged = name is not null && retryResults.TryGetValue(name, out var result)
                    ? (JsonObject)result.DeepClone()
                    : (JsonObject)test.DeepClone();
                merged["retries"] = 1;
                if (Status(merged) == "passed")
                    merged["flaky"] = true;
                tests[i] = merged;
            }

            if (report.Json["results"]?["summary"] is JsonObject summary)
            {
                var statuses = tests.OfType<JsonObject>().Select(Status).ToList();
                summary["tests"] = statuses.Count;
                foreach (var status in new[] { "passed", "failed", "pending", "skipped", "other" })
                    summary[status] = statuses.Count(s => s == status);
            }

            File.WriteAllText(report.Path, report.Json.ToJsonString());
        }
    }

    static string? Status(JsonObject test) =>
        test["status"]?.GetValueKind() == JsonValueKind.String ? test["status"]!.GetValue<string>() : null;

    /// <summary><c>**/</c> matches any number of directories, <c>*</c> and <c>?</c> stay within one.</summary>
    public static Regex GlobToRegex(string glob)
    {
        var pattern = Regex.Escape(glob.Replace('\\', '/'))
            .Replace(@"\*\*/", "")
            .Replace(@"\*\*", "")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]")
            .Replace("", "(?:.*/)?")
            .Replace("", ".*");
        return new Regex($"^{pattern}$", RegexOptions.CultureInvariant);
    }
}
