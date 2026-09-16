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
        string apiUrl, string repository, string token, long runId, int runAttempt, string runnerName, Action<string> log)
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
            jobStarted = JobStarted(jobs.RootElement, runnerName);
            if (jobStarted is null)
                log($"::warning::No in-progress job on runner '{runnerName}' in run {runId}, so the queue wait is unknown.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log($"::warning::Could not read the run's times from the Actions API, so the queue wait is unknown: {e.Message}");
        }

        return (runStarted, jobStarted);
    }

    /// <summary>
    /// The start of the job running on <paramref name="runnerName"/> that has not completed:
    /// the job calling this. Runner names are unique while a job runs.
    /// </summary>
    public static DateTimeOffset? JobStarted(JsonElement jobsResponse, string runnerName)
    {
        if (!jobsResponse.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            return null;

        return jobs.EnumerateArray()
            .Where(job => Text(job, "runner_name") == runnerName && Text(job, "status") != "completed")
            .Select(job => Time(job, "started_at"))
            .FirstOrDefault(started => started is not null);
    }

    static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static DateTimeOffset? Time(JsonElement element, string property) =>
        DateTimeOffset.TryParse(Text(element, property), out var time) ? time : null;
}
