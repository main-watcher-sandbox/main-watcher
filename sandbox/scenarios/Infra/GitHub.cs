using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MainWatcher.Scenarios.Infra;

/// <summary>A GitHub API call that failed, with its status and the start of GitHub's answer.</summary>
public sealed class GitHubException(HttpStatusCode status, string method, string path, string body)
    : Exception($"{method} {path}: HTTP {(int)status}: {(body.Length > 300 ? body[..300] + "…" : body)}")
{
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;
}

/// <summary>
/// The GitHub REST and GraphQL API, as the person running the suite (<c>gh auth token</c>): a sandbox organisation admin.
/// Every scenario polls through this one client, so it limits how many calls run at once, backs off when the hourly budget
/// runs low, and retries GitHub's transient failures.
/// </summary>
public sealed class GitHub : IDisposable
{
    readonly HttpClient http;
    readonly SemaphoreSlim slots = new(6);
    int remaining = int.MaxValue;
    DateTimeOffset reset = DateTimeOffset.MinValue;

    public GitHub(string token)
    {
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MainWatcher-Scenarios/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>API calls made so far, for the report.</summary>
    public int Calls => calls;
    int calls;

    public Task<JsonNode?> Get(string path, CancellationToken ct) => Send(HttpMethod.Get, path, null, ct);

    /// <summary>A GET that answers null for 404, for things that may not exist yet.</summary>
    public async Task<JsonNode?> Find(string path, CancellationToken ct)
    {
        try { return await Get(path, ct); }
        catch (GitHubException e) when (e.Status == HttpStatusCode.NotFound) { return null; }
    }

    public Task<JsonNode?> Post(string path, object? body, CancellationToken ct) => Send(HttpMethod.Post, path, body, ct);
    public Task<JsonNode?> Put(string path, object? body, CancellationToken ct) => Send(HttpMethod.Put, path, body, ct);
    public Task<JsonNode?> Patch(string path, object? body, CancellationToken ct) => Send(HttpMethod.Patch, path, body, ct);
    public Task<JsonNode?> Delete(string path, CancellationToken ct) => Send(HttpMethod.Delete, path, null, ct);

    /// <summary>Every item of a paged list. <paramref name="property"/> names the array when the page is an object.</summary>
    public async Task<List<JsonNode>> All(string path, CancellationToken ct, string? property = null, int maxPages = 10)
    {
        var items = new List<JsonNode>();
        var separator = path.Contains('?') ? '&' : '?';
        for (var page = 1; page <= maxPages; page++)
        {
            var node = await Get($"{path}{separator}per_page=100&page={page}", ct);
            var array = (property is null ? node : node?[property]) as JsonArray ?? [];
            items.AddRange(array.OfType<JsonNode>());
            if (array.Count < 100) break;
        }
        return items;
    }

    /// <summary>A GraphQL query or mutation. GraphQL reports errors in the body, so they are thrown here.</summary>
    public async Task<JsonNode> GraphQL(string query, object variables, CancellationToken ct)
    {
        var node = await Post("graphql", new { query, variables }, ct) ?? throw new InvalidDataException("GraphQL gave no body");
        if (node["errors"] is JsonArray { Count: > 0 } errors)
            throw new GitHubException(HttpStatusCode.OK, "POST", "graphql", errors.ToJsonString());
        return node["data"]!;
    }

    /// <summary>Plain text, such as a job's log. GitHub answers with a redirect to storage, which is followed.</summary>
    public async Task<string> Text(string path, CancellationToken ct) => System.Text.Encoding.UTF8.GetString(await Bytes(path, ct));

    /// <summary>A download, such as an artifact's zip, through GitHub's redirect to storage.</summary>
    public Task<byte[]> Bytes(string path, CancellationToken ct) => Request(HttpMethod.Get, path, null, ct);

    async Task<JsonNode?> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var content = await Request(method, path, body, ct);
        return content.Length == 0 ? null : JsonNode.Parse(content);
    }

    /// <summary>
    /// The one request policy, for API calls and downloads alike: the concurrency limit, the wait for the hourly budget, and
    /// retries of transport failures, GitHub's own errors and the secondary rate limit. Only the decoding differs.
    /// </summary>
    async Task<byte[]> Request(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await Budget(ct);
            await slots.WaitAsync(ct);
            HttpResponseMessage? response = null;
            byte[] content;
            try
            {
                Interlocked.Increment(ref calls);
                using var request = new HttpRequestMessage(method, path);
                if (body is not null)
                    request.Content = new StringContent(body is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(body),
                        Encoding.UTF8, "application/json");
                response = await http.SendAsync(request, ct);
                content = await response.Content.ReadAsByteArrayAsync(ct);
                Track(response);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested && attempt < 4)
            {
                response?.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
                continue;
            }
            finally { slots.Release(); }
            using (response)
            {
                if (response.IsSuccessStatusCode) return content;
                var text = Encoding.UTF8.GetString(content);
                var status = (int)response.StatusCode;
                // Transient: GitHub's own errors, and the secondary rate limit, which says how long to wait.
                if (attempt < 4 && (status >= 500 || status == 429
                    || status == 403 && text.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)))
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10 * attempt);
                    await Task.Delay(wait, ct);
                    continue;
                }
                throw new GitHubException(response.StatusCode, method.Method, path, text);
            }
        }
    }

    void Track(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-resource", out var resource) && resource.FirstOrDefault() != "core") return;
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && int.TryParse(left.FirstOrDefault(), out var value))
            remaining = value;
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var at) && long.TryParse(at.FirstOrDefault(), out var epoch))
            reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
    }

    /// <summary>Waits for the hourly budget to reset when little is left, rather than failing scenarios half-way.</summary>
    async Task Budget(CancellationToken ct)
    {
        if (remaining > 200) return;
        var wait = reset - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        if (wait <= TimeSpan.Zero) { remaining = int.MaxValue; return; }
        Console.Error.WriteLine($"GitHub API budget low ({remaining} left); waiting {wait.TotalMinutes:0} min for it to reset.");
        await Task.Delay(wait, ct);
        remaining = int.MaxValue;
    }

    /// <summary>The core budget left, for the report.</summary>
    public int Remaining => remaining;

    public void Dispose()
    {
        http.Dispose();
        slots.Dispose();
    }
}
