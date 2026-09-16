using System.Globalization;
using MainWatcher.TestRunner;

// Runs a target's tests inside the main-watcher-test step of run-integration-tests.yml.
// Writes finished=true only when the tests ran to completion, and exits with their exit code;
// the main-watcher-tests-finished marker step depends on that output (ADR-013).
// With the argument "timings", writes timings.json instead (ADR-011).

if (args is ["timings"])
    return await TimingsCommand.RunAsync(Console.WriteLine);

var command = Environment.GetEnvironmentVariable("MW_TEST_COMMAND");
var timeout = Environment.GetEnvironmentVariable("MW_TIMEOUT_MINUTES");
if (string.IsNullOrWhiteSpace(command)
    || !double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var timeoutMinutes)
    || timeoutMinutes <= 0)
{
    Console.WriteLine("::error title=Test runner misconfigured::MW_TEST_COMMAND and a positive MW_TIMEOUT_MINUTES are required.");
    return 2;
}

var result = await TestRunner.RunAsync(
    new TestRunOptions
    {
        Command = command,
        Timeout = TimeSpan.FromMinutes(timeoutMinutes),
        ResultsGlob = Environment.GetEnvironmentVariable("MW_RESULTS_GLOB") is { Length: > 0 } glob ? glob : CtrfReports.DefaultGlob,
        Shell = Environment.GetEnvironmentVariable("MW_SHELL") is { Length: > 0 } shell ? shell : "bash",
    },
    Console.WriteLine);

StepFiles.Append("GITHUB_OUTPUT", $"retry-count={result.RetryCount}\n" + (result.Finished ? "finished=true\n" : ""));
StepFiles.Append("GITHUB_STEP_SUMMARY", result switch
{
    { Finished: false } => "## Tests stopped at their timeout\n",
    { RetryCount: 1 } => $"## Tests finished with exit code {result.ExitCode} after retrying\n\n{string.Concat(result.RetriedTests.Select(t => $"- `{t}`\n"))}",
    _ => $"## Tests finished with exit code {result.ExitCode}\n",
});

return result.ExitCode;
