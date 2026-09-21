using System.Text.Json;
using System.Text.Json.Serialization;

namespace SampleTarget;

/// <summary>
/// The switches in <c>sandbox.json</c> at the repo root. Scenario tests steer the outcome of
/// a run by committing a change to that file. See README.md for what each switch does.
/// </summary>
public sealed record SandboxSwitches
{
    public const string FileName = "sandbox.json";

    [JsonPropertyName("failing_tests")] public string[] FailingTests { get; init; } = [];
    [JsonPropertyName("timed_tests")] public bool TimedTests { get; init; } = true;
    [JsonPropertyName("slow_suite_minutes")] public double SlowSuiteMinutes { get; init; }
    [JsonPropertyName("hang_test")] public bool HangTest { get; init; }
    [JsonPropertyName("flaky_test")] public bool FlakyTest { get; init; }
    [JsonPropertyName("inspect_environment")] public bool InspectEnvironment { get; init; }

    // Read by MSBuild (Directory.Build.props), not by tests.
    [JsonPropertyName("fail_restore")] public bool FailRestore { get; init; }

    // Stubs: read by the sandbox build of the reusable test workflow, not by tests.
    [JsonPropertyName("fail_upload")] public bool FailUpload { get; init; }
    [JsonPropertyName("hang_upload")] public bool HangUpload { get; init; }
    [JsonPropertyName("hang_upload_forever")] public bool HangUploadForever { get; init; }

    // Read by the sandbox-slow-check workflow, not by tests.
    [JsonPropertyName("slow_check_minutes")] public double SlowCheckMinutes { get; init; }

    public static SandboxSwitches Current { get; } = Load();

    /// <summary>Where <c>sandbox.json</c> was found: the repo root.</summary>
    public static string FilePath => Find(AppContext.BaseDirectory)
        ?? throw new FileNotFoundException($"{FileName} not found above {AppContext.BaseDirectory}");

    public bool ShouldFail(string testName) =>
        FailingTests.Contains("*") || FailingTests.Contains(testName, StringComparer.OrdinalIgnoreCase);

    private static SandboxSwitches Load()
    {
        var path = FilePath;

        return JsonSerializer.Deserialize<SandboxSwitches>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
               ?? throw new InvalidDataException($"{path} is empty");
    }

    private static string? Find(string directory)
    {
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
