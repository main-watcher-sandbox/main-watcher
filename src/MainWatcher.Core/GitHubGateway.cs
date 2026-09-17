using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MainWatcher.Core;

/// <summary>Shared GitHub model and REST boundary. The caller supplies an installation token.</summary>
public sealed class GitHubGateway(HttpClient http, long appId,
    Func<TimeSpan, CancellationToken, Task>? delay = null, Action<string>? log = null) : IGitHubGateway
{
    public const string CheckName = "main-watcher";
    public const string Workflow = "main-watcher-tests.yml";
    /// <summary>The watcher's own workflow in the watcher repo, dispatched per target and swept hourly (ADR-010).</summary>
    public const string WatchWorkflow = "watch.yml";
    /// <summary>The prefix of a per-target <c>watch.yml</c> <c>run-name</c>: how a cycle for one target is recognised.</summary>
    public const string WatchRunPrefix = "watch ";
    /// <summary>The gate workflow targets copy from <c>templates/main-watcher-gate.yml</c>.</summary>
    public const string GateWorkflow = "main-watcher-gate.yml";
    /// <summary>The gate template's job that runs only when the gate could not enforce a lock (ADR-008).</summary>
    public const string FailOpenJob = "main-watcher/gate-fail-open";
    /// <summary>At most this many gate runs are examined in one sweep, newest first, so a busy queue cannot lengthen it.</summary>
    public const int FailOpenRuns = 50;
    readonly Dictionary<string, (string Head, List<CheckRun> Checks)> snapshots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An installation token for the App this gateway authenticates as (a JWT client), limited to <paramref name="repo"/>.
    /// </summary>
    public async Task<InstallationToken> InstallationToken(string repo, CancellationToken ct)
    {
        var installation = (await Send(HttpMethod.Get, $"repos/{repo}/installation", null, ct)).GetProperty("id").GetInt64();
        var json = await Send(HttpMethod.Post, $"app/installations/{installation}/access_tokens",
            new { repositories = new[] { repo.Split('/')[1] } }, ct);
        return new(json.GetProperty("token").GetString()!, Date(json, "expires_at")
            ?? throw new InvalidDataException("GitHub returned an installation token without expires_at."));
    }

    public async Task ValidateTarget(Target target, CancellationToken ct)
    {
        var file = await Send(HttpMethod.Get, $"repos/{target.Repo}/contents/.github/workflows/{Workflow}?ref=main", null, ct);
        var yaml = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(file.GetProperty("content").GetString()!));
        CallerConfiguration.Validate(target, yaml);
    }

    async Task<JsonElement> Send(HttpMethod method, string path, object? body, CancellationToken ct, Action<string?>? nextPage = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, path);
                if (body is not null) request.Content = JsonContent.Create(body);
                using var response = await http.SendAsync(request, ct);
                if (method == HttpMethod.Get && attempt < 3 && IsTransient(response))
                {
                    await (delay ?? Task.Delay)(TimeSpan.FromSeconds(2 * Math.Pow(2, attempt)), ct);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync(ct);
                nextPage?.Invoke(NextPage(response));
                if (string.IsNullOrWhiteSpace(text)) return default;
                using var document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
            catch (HttpRequestException e) when (method == HttpMethod.Get && attempt < 3 && e.StatusCode is null) { }
            catch (TaskCanceledException) when (method == HttpMethod.Get && attempt < 3 && !ct.IsCancellationRequested) { }
            await (delay ?? Task.Delay)(TimeSpan.FromSeconds(2 * Math.Pow(2, attempt)), ct);
        }
    }

    static bool IsTransient(HttpResponseMessage response) => (int)response.StatusCode >= 500
        || response.StatusCode == HttpStatusCode.TooManyRequests
        || response.StatusCode == HttpStatusCode.Forbidden && (response.Headers.RetryAfter is not null
            || response.Headers.TryGetValues("x-ratelimit-remaining", out var values) && values.Contains("0"));

    static string? NextPage(HttpResponseMessage response) => response.Headers.TryGetValues("Link", out var links)
        ? links.Select(link => System.Text.RegularExpressions.Regex.Match(link, "<([^>]+)>;\\s*rel=\"next\""))
            .FirstOrDefault(match => match.Success)?.Groups[1].Value : null;

    async Task<List<JsonElement>> Pages(string path, string? key, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        await foreach (var item in Items(path, key, ct)) all.Add(item);
        return all;
    }

    async IAsyncEnumerable<JsonElement> Items(string path, string? key,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? next = $"{path}{(path.Contains('?') ? '&' : '?')}per_page=100&page=1";
        while (next is not null)
        {
            var uri = new Uri(http.BaseAddress!, next);
            if (uri.GetLeftPart(UriPartial.Authority) != http.BaseAddress!.GetLeftPart(UriPartial.Authority))
                throw new InvalidDataException("GitHub pagination link points outside the API origin.");
            var json = await Send(HttpMethod.Get, uri.AbsoluteUri, null, ct, value => next = value);
            var items = (key is null ? json : json.GetProperty(key)).EnumerateArray().ToArray();
            foreach (var item in items) yield return item;
        }
    }

    static string? Text(JsonElement json, string key) => json.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static DateTimeOffset? Date(JsonElement json, string key) => DateTimeOffset.TryParse(Text(json, key), out var date) ? date : null;
    static CheckRun Check(JsonElement json) => new(json.GetProperty("id").GetInt64(), Text(json, "head_sha")!,
        Text(json, "status")!, Text(json, "conclusion"), Date(json, "started_at") ?? DateTimeOffset.MinValue,
        Date(json, "completed_at"), Text(json, "external_id"),
        json.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object ? Text(output, "title") : null);

    public async Task<string> MainHead(string repo, CancellationToken ct) =>
        (await Send(HttpMethod.Get, $"repos/{repo}/commits/main", null, ct)).GetProperty("sha").GetString()!;

    public async Task<IReadOnlyList<CheckRun>> Checks(string repo, CancellationToken ct)
    {
        // Bootstrap once per gateway lifetime; subsequent worker polls refresh only
        // pending checks, the head, and commits added since the previous snapshot.
        var head = await MainHead(repo, ct);
        var hasSnapshot = snapshots.TryGetValue(repo, out var snapshot);
        var result = hasSnapshot ? snapshot.Checks.ToList() : [];
        var refresh = result.Where(c => c.Status != "completed").Select(c => c.Sha).ToHashSet(StringComparer.Ordinal);
        if (hasSnapshot) refresh.Add(snapshot.Head);
        refresh.Add(head);
        if (!hasSnapshot || snapshot.Head != head)
        {
            await foreach (var commit in Items($"repos/{repo}/commits?sha={head}", null, ct))
            {
                var sha = commit.GetProperty("sha").GetString()!;
                if (hasSnapshot && sha == snapshot.Head) break;
                refresh.Add(sha);
            }
        }
        foreach (var sha in refresh)
        {
            var checks = await CommitChecks(repo, sha, ct);
            result.RemoveAll(c => c.Sha == sha);
            result.AddRange(checks);
        }
        // Publish only a fully refreshed snapshot; failed reads never make partial state authoritative.
        snapshots[repo] = (head, result);
        return result;
    }

    public async Task<IReadOnlyList<CheckRun>> CommitChecks(string repo, string sha, CancellationToken ct) =>
        (await Pages($"repos/{repo}/commits/{sha}/check-runs?check_name={CheckName}&filter=all", "check_runs", ct))
        .Where(c => c.GetProperty("app").GetProperty("id").GetInt64() == appId).Select(Check).ToArray();

    public async Task<IReadOnlyList<string>> History(string repo, string sha, int limit, CancellationToken ct)
    {
        var shas = new List<string>();
        await foreach (var commit in Items($"repos/{repo}/commits?sha={sha}", null, ct))
        {
            shas.Add(commit.GetProperty("sha").GetString()!);
            // Stop before reading a page that would not be used.
            if (shas.Count == limit) break;
        }
        return shas;
    }

    public async Task<IReadOnlyList<Push>> Pushes(string repo, int limit, CancellationToken ct)
    {
        var pushes = new List<Push>();
        // Activity pages by cursor; the next link carries it.
        await foreach (var entry in Items($"repos/{repo}/activity?ref=main&direction=desc", null, ct))
        {
            pushes.Add(new(Text(entry, "before") ?? "", Text(entry, "after") ?? "", Date(entry, "timestamp") ?? DateTimeOffset.MinValue,
                Text(entry, "activity_type") ?? "", entry.TryGetProperty("actor", out var actor) && actor.ValueKind == JsonValueKind.Object ? Text(actor, "login") : null));
            if (pushes.Count == limit) break;
        }
        return pushes;
    }

    public async Task<int?> CommitCount(string repo, string before, string after, CancellationToken ct)
    {
        if (!IsSha(before) || !IsSha(after) || before.All(c => c == '0') || after.All(c => c == '0')) return null;
        try
        {
            // One commit per page keeps the response small; total_commits still counts them all.
            var json = await Send(HttpMethod.Get, $"repos/{repo}/compare/{before}...{after}?per_page=1", null, ct);
            return json.TryGetProperty("total_commits", out var total) ? total.GetInt32() : null;
        }
        // A force push can leave "before" unreachable, and GitHub cannot compare it any more.
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) { return null; }
    }

    static bool IsSha(string text) => text.Length == 40 && text.All(char.IsAsciiHexDigit);

    public async Task<CheckRun> CreateCheck(string repo, string sha, DateTimeOffset now, CancellationToken ct) =>
        Check(await Send(HttpMethod.Post, $"repos/{repo}/check-runs", new { name = CheckName, head_sha = sha, status = "in_progress", started_at = now }, ct));

    public async Task<long?> Dispatch(string repo, string sha, long checkId, CancellationToken ct)
    {
        try
        {
            var json = await Send(HttpMethod.Post, $"repos/{repo}/actions/workflows/{Workflow}/dispatches",
                new { @ref = "main", return_run_details = true, inputs = new { sha, check_run_id = checkId.ToString(System.Globalization.CultureInfo.InvariantCulture) } }, ct);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("workflow_run_id", out var id)) return id.GetInt64();
            return NoRun(repo, checkId, "the response carried no workflow_run_id");
        }
        // The reason is only logged: the POST is never retried, and recovery links or releases the check.
        catch (HttpRequestException e) when (e.StatusCode is null || (int)e.StatusCode >= 500)
        {
            return NoRun(repo, checkId, e.StatusCode is { } status ? $"HTTP {(int)status} ({e.Message})" : $"network error ({e.Message})");
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { return NoRun(repo, checkId, $"timed out ({e.Message})"); }
    }

    public async Task DispatchWorkflow(string repo, string workflow, IReadOnlyDictionary<string, string> inputs, CancellationToken ct) =>
        // Never retried: a lost response may still have started the workflow.
        await Send(HttpMethod.Post, $"repos/{repo}/actions/workflows/{workflow}/dispatches", new { @ref = "main", inputs }, ct);

    long? NoRun(string repo, long checkId, string reason)
    {
        log?.Invoke($"Check {checkId}: dispatch of {Workflow} in {repo} returned no run: {reason}.");
        return null;
    }

    public async Task<IReadOnlyList<WorkflowRun>> Runs(string repo, string workflow, DateTimeOffset since, CancellationToken ct) =>
        (await Pages($"repos/{repo}/actions/workflows/{workflow}/runs?event=workflow_dispatch&created={Uri.EscapeDataString(">=" + since.ToString("O"))}", "workflow_runs", ct))
        .Select(r => new WorkflowRun(r.GetProperty("id").GetInt64(), Text(r, "display_title")!, Date(r, "created_at")!.Value, Text(r, "status")!, Date(r, "updated_at"))).ToArray();

    public async Task<IReadOnlyList<FailOpen>> FailOpens(string repo, DateTimeOffset since, CancellationToken ct)
    {
        List<JsonElement> runs;
        try
        {
            runs = await Pages($"repos/{repo}/actions/workflows/{GateWorkflow}/runs?event=merge_group&created={Uri.EscapeDataString(">=" + since.ToString("O"))}", "workflow_runs", ct);
        }
        // A target that has not copied the gate workflow, or has renamed it, has no such runs to report.
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return []; }
        var found = new List<FailOpen>();
        foreach (var run in runs.OrderByDescending(r => r.GetProperty("id").GetInt64()).Take(FailOpenRuns))
        {
            var id = run.GetProperty("id").GetInt64();
            // The template skips this job unless the gate failed open, so a job with any other conclusion, or none yet, is one.
            var jobs = await Pages($"repos/{repo}/actions/runs/{id}/jobs?filter=latest", "jobs", ct);
            if (!jobs.Any(j => Text(j, "name") == FailOpenJob && Text(j, "conclusion") != "skipped")) continue;
            found.Add(new(id, Text(run, "head_sha") ?? "", Text(run, "head_branch") ?? "", Date(run, "created_at") ?? since));
        }
        return found;
    }

    public async Task Link(string repo, long checkId, long runId, CancellationToken ct) =>
        await Send(HttpMethod.Patch, $"repos/{repo}/check-runs/{checkId}", new { external_id = runId.ToString(System.Globalization.CultureInfo.InvariantCulture), details_url = $"https://github.com/{repo}/actions/runs/{runId}" }, ct);

    public async Task<IReadOnlyList<WorkflowJob>?> Jobs(string repo, long runId, CancellationToken ct)
    {
        try
        {
            var jobs = await Pages($"repos/{repo}/actions/runs/{runId}/jobs?filter=latest", "jobs", ct);
            var parsed = jobs.Select(j => new WorkflowJob(Text(j, "name")!, Text(j, "status")!,
                j.TryGetProperty("steps", out var steps) ? steps.EnumerateArray().Select(s => new JobStep(Text(s, "name")!, Text(s, "conclusion"))).ToArray() : [],
                Date(j, "completed_at"))).ToArray();
            if (!parsed.Any(j => Outcomes.IsTestJob(j.Name)))
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
            if (response.Content.Headers.ContentLength > CtrfReader.MaxReportBytes) return CtrfResult.Unknown;
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > CtrfReader.MaxReportBytes) return CtrfResult.Unknown;
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            return CtrfReader.ReadZip(buffer);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException) { return CtrfResult.Unknown; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return CtrfResult.Unknown; }
    }

    public async Task Complete(string repo, long checkId, string conclusion, string title, string summary, CancellationToken ct) =>
        await Send(HttpMethod.Patch, $"repos/{repo}/check-runs/{checkId}", new
        {
            status = "completed", conclusion, completed_at = DateTimeOffset.UtcNow,
            output = new { title, summary }
        }, ct);

    public async Task<string?> File(string repo, string path, CancellationToken ct)
    {
        try
        {
            var file = await Send(HttpMethod.Get, $"repos/{repo}/contents/{path}?ref=main", null, ct);
            // A directory is an array, not a file. An existing empty file is "", not null, so a
            // CODEOWNERS lookup stops at the first file that exists. Files over 1 MB come without
            // inline content and also read as "".
            if (file.ValueKind != JsonValueKind.Object || Text(file, "type") != "file") return null;
            return Text(file, "encoding") == "base64" && Text(file, "content") is { } content
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content)) : "";
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    static Issue ToIssue(JsonElement json) => new(json.GetProperty("number").GetInt32(), Text(json, "title") ?? "", Text(json, "body"),
        Text(json.GetProperty("user"), "login") ?? "", Text(json.GetProperty("user"), "type") ?? "", Text(json, "html_url") ?? "",
        Text(json, "state") ?? "open", Text(json, "state_reason"), Date(json, "updated_at"),
        json.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0);

    static string Since(DateTimeOffset since) => Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));

    public async Task<IReadOnlyList<Issue>> OpenIssues(string repo, string label, CancellationToken ct) =>
        (await Pages($"repos/{repo}/issues?state=open&labels={Uri.EscapeDataString(label)}", null, ct))
        .Where(i => !i.TryGetProperty("pull_request", out _)).Select(ToIssue).ToArray();

    public async Task<IReadOnlyList<Issue>> Issues(string repo, string label, DateTimeOffset since, CancellationToken ct) =>
        (await Pages($"repos/{repo}/issues?state=all&labels={Uri.EscapeDataString(label)}&since={Since(since)}", null, ct))
        .Where(i => !i.TryGetProperty("pull_request", out _)).Select(ToIssue).ToArray();

    public async Task<IReadOnlyList<IssueComment>> Comments(string repo, int number, DateTimeOffset? since, CancellationToken ct) =>
        (await Pages($"repos/{repo}/issues/{number}/comments" + (since is { } time ? $"?since={Since(time)}" : ""), null, ct))
        .Select(c => c.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
            ? new IssueComment(Text(c, "body") ?? "", Text(user, "login") ?? "", Text(user, "type") ?? "")
            : new IssueComment(Text(c, "body") ?? "", "", "")).ToArray();

    public async Task<Account?> ClosedBy(string repo, int number, CancellationToken ct)
    {
        // The issue list omits closed_by; only a single issue carries it.
        var issue = await Send(HttpMethod.Get, $"repos/{repo}/issues/{number}", null, ct);
        return issue.TryGetProperty("closed_by", out var user) && user.ValueKind == JsonValueKind.Object && Text(user, "login") is { } login
            ? new(login, Text(user, "type") ?? "") : null;
    }

    public async Task EditBody(string repo, int number, string body, CancellationToken ct) =>
        await Send(HttpMethod.Patch, $"repos/{repo}/issues/{number}", new { body }, ct);

    public async Task<Issue> CreateIssue(string repo, string title, string body, string label, CancellationToken ct)
    {
        try { await Send(HttpMethod.Post, $"repos/{repo}/labels", new { name = label, color = "b60205" }, ct); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.UnprocessableEntity) { }
        return ToIssue(await Send(HttpMethod.Post, $"repos/{repo}/issues", new { title, body, labels = new[] { label } }, ct));
    }

    public async Task Comment(string repo, int number, string body, CancellationToken ct) =>
        await Send(HttpMethod.Post, $"repos/{repo}/issues/{number}/comments", new { body }, ct);

    public async Task Close(string repo, int number, string reason, long? duplicateOf, CancellationToken ct) =>
        // duplicate_issue_id takes the database ID: in the sandbox, an issue number linked an unrelated issue.
        await Send(HttpMethod.Patch, $"repos/{repo}/issues/{number}", duplicateOf is { } canonical
            ? new { state = "closed", state_reason = reason, duplicate_issue_id = canonical }
            : new { state = "closed", state_reason = reason }, ct);
}
