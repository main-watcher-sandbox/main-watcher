using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MainWatcher.Core;

/// <summary>Shared GitHub model and REST boundary. The caller supplies an installation token.</summary>
public sealed class GitHubGateway(HttpClient http, long appId) : IGitHubGateway
{
    public const string CheckName = "main-watcher";
    public const string Workflow = "main-watcher-tests.yml";

    async Task<JsonElement> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return default;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    async Task<List<JsonElement>> Pages(string path, string? key, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        for (var page = 1; ; page++)
        {
            var json = await Send(HttpMethod.Get, $"{path}{(path.Contains('?') ? '&' : '?')}per_page=100&page={page}", null, ct);
            var items = (key is null ? json : json.GetProperty(key)).EnumerateArray().ToArray();
            all.AddRange(items);
            if (items.Length < 100) return all;
        }
    }

    static string? Text(JsonElement json, string key) => json.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static DateTimeOffset? Date(JsonElement json, string key) => DateTimeOffset.TryParse(Text(json, key), out var date) ? date : null;
    static CheckRun Check(JsonElement json) => new(json.GetProperty("id").GetInt64(), Text(json, "head_sha")!,
        Text(json, "status")!, Text(json, "conclusion"), Date(json, "started_at") ?? DateTimeOffset.MinValue,
        Date(json, "completed_at"), Text(json, "external_id"));

    public async Task<string> MainHead(string repo, CancellationToken ct) =>
        (await Send(HttpMethod.Get, $"repos/{repo}/commits/main", null, ct)).GetProperty("sha").GetString()!;

    public async Task<IReadOnlyList<CheckRun>> Checks(string repo, CancellationToken ct)
    {
        // Checks are commit-scoped in GitHub. Walk main's history so an older active run
        // also blocks dispatch and poll_interval applies across head changes.
        var result = new List<CheckRun>();
        foreach (var commit in await Pages($"repos/{repo}/commits?sha=main", null, ct))
        {
            var sha = commit.GetProperty("sha").GetString();
            var checks = await Pages($"repos/{repo}/commits/{sha}/check-runs?check_name={CheckName}&filter=all", "check_runs", ct);
            result.AddRange(checks.Where(c => c.GetProperty("app").GetProperty("id").GetInt64() == appId).Select(Check));
        }
        return result;
    }

    public async Task<CheckRun> CreateCheck(string repo, string sha, DateTimeOffset now, CancellationToken ct) =>
        Check(await Send(HttpMethod.Post, $"repos/{repo}/check-runs", new { name = CheckName, head_sha = sha, status = "in_progress", started_at = now }, ct));

    public async Task<long?> Dispatch(string repo, string sha, long checkId, CancellationToken ct)
    {
        try
        {
            var json = await Send(HttpMethod.Post, $"repos/{repo}/actions/workflows/{Workflow}/dispatches",
                new { @ref = "main", return_run_details = true, inputs = new { sha, check_run_id = checkId.ToString(System.Globalization.CultureInfo.InvariantCulture) } }, ct);
            return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("workflow_run_id", out var id) ? id.GetInt64() : null;
        }
        catch (HttpRequestException e) when (e.StatusCode is null || (int)e.StatusCode >= 500) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public async Task<IReadOnlyList<WorkflowRun>> Runs(string repo, DateTimeOffset since, CancellationToken ct) =>
        (await Pages($"repos/{repo}/actions/workflows/{Workflow}/runs?event=workflow_dispatch&created={Uri.EscapeDataString(">=" + since.ToString("O"))}", "workflow_runs", ct))
        .Select(r => new WorkflowRun(r.GetProperty("id").GetInt64(), Text(r, "display_title")!, Date(r, "created_at")!.Value, Text(r, "status")!)).ToArray();

    public async Task Link(string repo, long checkId, long runId, CancellationToken ct) =>
        await Send(HttpMethod.Patch, $"repos/{repo}/check-runs/{checkId}", new { external_id = runId.ToString(System.Globalization.CultureInfo.InvariantCulture), details_url = $"https://github.com/{repo}/actions/runs/{runId}" }, ct);

    public async Task<IReadOnlyList<WorkflowJob>?> Jobs(string repo, long runId, CancellationToken ct)
    {
        try
        {
            var jobs = await Pages($"repos/{repo}/actions/runs/{runId}/jobs?filter=latest", "jobs", ct);
            var parsed = jobs.Select(j => new WorkflowJob(Text(j, "name")!, Text(j, "status")!,
                j.TryGetProperty("steps", out var steps) ? steps.EnumerateArray().Select(s => new JobStep(Text(s, "name")!, Text(s, "conclusion"))).ToArray() : [])).ToArray();
            if (!parsed.Any(j => j.Name == "main-watcher" || j.Name.EndsWith(" / main-watcher", StringComparison.Ordinal)))
            {
                var run = await Send(HttpMethod.Get, $"repos/{repo}/actions/runs/{runId}", null, ct);
                if (Text(run, "status") == "completed") return [];
                // No job has been materialized yet. Preserve the pending state.
                return [new("main-watcher", "queued", [])];
            }
            return parsed;
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<CtrfResult> Reports(string repo, long runId, CancellationToken ct)
    {
        try
        {
            var artifacts = (await Pages($"repos/{repo}/actions/runs/{runId}/artifacts", "artifacts", ct))
                .Where(a => Text(a, "name") == "main-watcher-ctrf" && !a.GetProperty("expired").GetBoolean()).ToArray();
            if (artifacts.Length != 1) return CtrfResult.Unknown;
            var id = artifacts[0].GetProperty("id").GetInt64();
            using var response = await http.GetAsync($"repos/{repo}/actions/artifacts/{id}/zip", HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 32 * 1024 * 1024) return CtrfResult.Unknown;
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > 32 * 1024 * 1024) return CtrfResult.Unknown;
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            return CtrfReader.ReadZip(buffer);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException) { return CtrfResult.Unknown; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return CtrfResult.Unknown; }
    }

    public async Task Complete(string repo, long checkId, string conclusion, string summary, CancellationToken ct) =>
        await Send(HttpMethod.Patch, $"repos/{repo}/check-runs/{checkId}", new
        {
            status = "completed", conclusion, completed_at = DateTimeOffset.UtcNow,
            output = new { title = conclusion == "success" ? "Tests passed" : conclusion == "failure" ? "Tests failed" : "Outcome unknown", summary }
        }, ct);
}
