using System.Globalization;
using System.Text.Json;

namespace MainWatcher.TestRunner;

/// <summary>
/// <c>MainWatcher.TestRunner timings</c>: the step of run-integration-tests.yml that writes
/// <c>timings.json</c> next to the CTRF reports, so it is uploaded in <c>main-watcher-ctrf</c>
/// (ADR-011). Its inputs come from the environment the workflow sets.
/// </summary>
public static class TimingsCommand
{
    public static async Task<int> RunAsync(Action<string> log)
    {
        var workingDirectory = Environment.CurrentDirectory;
        long? runId = long.TryParse(Env("GITHUB_RUN_ID"), out var id) ? id : null;
        int? runAttempt = int.TryParse(Env("GITHUB_RUN_ATTEMPT"), out var attempt) ? attempt : null;

        DateTimeOffset? runStarted = null, jobStarted = null;
        if (runId is { } run && runAttempt is { } a && Env("GITHUB_TOKEN") is { Length: > 0 } token)
        {
            (runStarted, jobStarted) = await RunTimes.FetchAsync(
                Env("GITHUB_API_URL") ?? "https://api.github.com", Env("GITHUB_REPOSITORY") ?? "", token, run, a, Env("RUNNER_NAME") ?? "", log);
        }

        var reports = CtrfReports.Load(workingDirectory, Env("MW_RESULTS_GLOB") is { Length: > 0 } glob ? glob : CtrfReports.DefaultGlob, log);
        var timings = Timings.Build(
            new TimingsInput
            {
                Sha = Env("MW_SHA") ?? "",
                RunId = runId,
                RunAttempt = runAttempt,
                RunStarted = runStarted,
                JobStarted = jobStarted,
                RestoreStarted = EpochMilliseconds("MW_RESTORE_STARTED_MS"),
                RestoreCompleted = EpochMilliseconds("MW_RESTORE_COMPLETED_MS"),
                TestStarted = EpochMilliseconds("MW_TEST_STARTED_MS"),
                TestCompleted = EpochMilliseconds("MW_TEST_COMPLETED_MS"),
                Retried = Env("MW_RETRY_COUNT") == "1",
            },
            reports);

        var json = timings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(workingDirectory, Timings.FileName), json);
        log(json);
        StepFiles.Append("GITHUB_STEP_SUMMARY", "\n### Timings\n\n" + Timings.Summary(timings));
        return 0;
    }

    static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>A time the workflow recorded with <c>date +%s%3N</c>.</summary>
    static DateTimeOffset? EpochMilliseconds(string name) =>
        long.TryParse(Env(name), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}

/// <summary>The files GitHub Actions reads step outputs and the job summary from.</summary>
public static class StepFiles
{
    public static void Append(string fileVariable, string text)
    {
        if (Environment.GetEnvironmentVariable(fileVariable) is { Length: > 0 } path)
            File.AppendAllText(path, text);
    }
}
