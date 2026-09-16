using System.Net.Http.Headers;
using System.Text.Json;

namespace MainWatcher.TestRunner;

/// <summary>
/// When the workflow run started and when this job got a runner, for the queue wait in
/// <c>timings.json</c>. Read through the Actions API, which needs <c>actions: read</c>. Timings
/// are best effort: any failure gives nulls and a warning, never a failed step.
/// </summary>
public static class RunTimes
{
    public static async Task<(DateTimeOffset? RunStarted, DateTimeOffset? JobStarted)> FetchAsync(
        string apiUrl, string repository, string token, long runId, int runAttempt, long? jobId, string runnerName, Action<string> log)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("main-watcher-test-runner", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        DateTimeOffset? runStarted = null, jobStarted = null;
        try
        {
            using var run = JsonDocument.Parse(await http.GetStringAsync($"{apiUrl}/repos/{repository}/actions/runs/{runId}"));
            runStarted = Time(run.RootElement, "run_started_at");

            using var jobs = JsonDocument.Parse(await http.GetStringAsync(
                $"{apiUrl}/repos/{repository}/actions/runs/{runId}/attempts/{runAttempt}/jobs?per_page=100"));
            jobStarted = JobStarted(jobs.RootElement, jobId, runnerName);
            if (jobStarted is null)
                log($"::warning::This job (ID {jobId?.ToString() ?? "unknown"}, runner '{runnerName}') was not found once in run {runId}, so the queue wait is unknown.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log($"::warning::Could not read the run's times from the Actions API, so the queue wait is unknown: {e.Message}");
        }

        return (runStarted, jobStarted);
    }

    /// <summary>
    /// The start of the job calling this. It is found by <paramref name="jobId"/>, the job's
    /// <c>job.check_run_id</c>, which the jobs API returns as the job's <c>id</c>. Without an ID,
    /// it falls back to the one unfinished job on <paramref name="runnerName"/>. Runner names can
    /// repeat within a run when repository and organisation runners share a name, so more than
    /// one match gives null rather than another job's start.
    /// </summary>
    public static DateTimeOffset? JobStarted(JsonElement jobsResponse, long? jobId, string runnerName)
    {
        if (!jobsResponse.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            return null;

        var matches = jobId is { } id
            ? jobs.EnumerateArray().Where(job => job.TryGetProperty("id", out var value) && value.TryGetInt64(out var jobIdValue) && jobIdValue == id).ToList()
            : jobs.EnumerateArray().Where(job => Text(job, "runner_name") == runnerName && Text(job, "status") != "completed").ToList();

        return matches.Count == 1 ? Time(matches[0], "started_at") : null;
    }

    static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static DateTimeOffset? Time(JsonElement element, string property) =>
        DateTimeOffset.TryParse(Text(element, property), out var time) ? time : null;
}
