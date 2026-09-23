using System.Text.Json.Nodes;

namespace MainWatcher.TestRunner.Tests;

/// <summary>
/// A temporary working directory standing in for a target checkout, with fake test commands
/// run through bash.
/// </summary>
public sealed class FakeTarget : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("mw-test-runner-").FullName;

    /// <summary>Git for Windows' bash locally; the runner's bash on Linux.</summary>
    public static string Bash { get; } =
        OperatingSystem.IsWindows() && File.Exists(@"C:\Program Files\Git\bin\bash.exe") ? @"C:\Program Files\Git\bin\bash.exe" : "bash";

    public TestRunOptions Options(string command, TimeSpan? timeout = null) => new()
    {
        Command = command,
        Timeout = timeout ?? TimeSpan.FromMinutes(1),
        WorkingDirectory = Root,
        Shell = Bash,
    };

    public string PathOf(string relative) => Path.Combine(Root, relative);

    public void Write(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// A fake <c>dotnet test</c>: copies <c>first/*.ctrf.json</c> into
    /// <c>&lt;project&gt;/TestResults/</c> and exits <paramref name="firstExitCode"/>; when called
    /// with <c>--filter-method</c>, saves its arguments to <c>retry-args.txt</c>, copies
    /// <c>retry/*.ctrf.json</c> instead and exits <paramref name="retryExitCode"/>. Returns the command.
    /// With <paramref name="sharedResults"/>, every report goes to the one <c>TestResults/</c> at the root instead, as
    /// xUnit 4.x writes them (ADR-021).
    /// </summary>
    public string FakeDotnetTest(int firstExitCode, int retryExitCode, string retryExtra = "", bool sharedResults = false)
    {
        Write("fake-test.sh", $$"""
            if [[ " $* " == *" --filter-method "* ]]; then
              printf '%s\n' "$@" > retry-args.txt
              src=retry; code={{retryExitCode}}
              {{retryExtra}}
            else
              src=first; code={{firstExitCode}}
            fi
            for f in "$src"/*.ctrf.json; do
              [ -e "$f" ] || continue
              {{(sharedResults ? "dir=TestResults" : "dir=\"$(basename \"$f\" .ctrf.json)/TestResults\"")}}
              mkdir -p "$dir"
              cp "$f" "$dir/"
            done
            exit "$code"
            """);
        return "bash fake-test.sh";
    }

    public JsonObject ReadReport(string project) =>
        JsonNode.Parse(File.ReadAllText(PathOf($"{project}/TestResults/{project}.ctrf.json")))!.AsObject();

    public static string Report(params (string Type, string Method, string Status)[] tests) =>
        new JsonObject
        {
            ["reportFormat"] = "CTRF",
            ["results"] = new JsonObject
            {
                ["tool"] = new JsonObject { ["name"] = "xUnit.net v3" },
                ["summary"] = new JsonObject
                {
                    ["tests"] = tests.Length,
                    ["passed"] = tests.Count(t => t.Status == "passed"),
                    ["failed"] = tests.Count(t => t.Status == "failed"),
                    ["pending"] = 0,
                    ["skipped"] = tests.Count(t => t.Status == "skipped"),
                    ["other"] = 0,
                },
                ["tests"] = new JsonArray(tests.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = $"{t.Type}.{t.Method}",
                    ["status"] = t.Status,
                    ["duration"] = 5,
                    ["extra"] = new JsonObject { ["type"] = t.Type, ["method"] = t.Method },
                }).ToArray()),
            },
        }.ToJsonString();

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
