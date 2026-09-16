using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MainWatcher.Gate;

/// <summary>A GitHub API call failed after its retries, or with an error that retrying cannot fix.</summary>
public sealed class GitHubApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Read-only GitHub REST calls. A transient failure (network error, timeout, 5xx, rate limit)
/// is retried <see cref="Retries"/> times with exponential backoff (ADR-008).
/// </summary>
public sealed class GitHubApi(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null, Action<string>? log = null)
{
    public const int Retries = 3;

    static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);
    static readonly Regex NextLink = new("<(?<url>[^>]+)>;\\s*rel=\"next\"");

    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<JsonElement> GetAsync(string url, CancellationToken ct) => (await SendAsync(url, ct)).Body;

    /// <summary>Gets a JSON array endpoint, following <c>Link: rel="next"</c> pages.</summary>
    public async Task<List<JsonElement>> GetAllPagesAsync(string url, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        for (string? next = url; next is not null;)
        {
            var (body, nextUrl) = await SendAsync(next, ct);
            if (body.ValueKind != JsonValueKind.Array)
                throw new GitHubApiException($"GET {next}: expected a JSON array");
            items.AddRange(body.EnumerateArray());
            next = nextUrl;
        }
        return items;
    }

    async Task<(JsonElement Body, string? Next)> SendAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            string failure;
            Exception? inner = null;
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    using var document = JsonDocument.Parse(json);
                    return (document.RootElement.Clone(), NextPage(response));
                }

                failure = $"HTTP {(int)response.StatusCode}";
                if (!IsTransient(response))
                    throw new GitHubApiException($"GET {url}: {failure}");
            }
            catch (HttpRequestException e)
            {
                (failure, inner) = (e.Message, e);
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                (failure, inner) = ("timed out", e);
            }
            catch (JsonException e)
            {
                (failure, inner) = ("invalid JSON", e);
            }

            if (attempt == Retries)
                throw new GitHubApiException($"GET {url}: {failure}, after {Retries} retries", inner);

            var backoff = FirstBackoff * Math.Pow(2, attempt);
            log?.Invoke($"GET {url}: {failure}; retry {attempt + 1} of {Retries} in {backoff.TotalSeconds:0} s");
            await _delay(backoff, ct);
        }
    }

    static bool IsTransient(HttpResponseMessage response) =>
        (int)response.StatusCode >= 500
        || response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden
            && (response.Headers.RetryAfter is not null
                || (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.Contains("0"))));

    static string? NextPage(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Link", out var links)
            ? links.Select(link => NextLink.Match(link)).FirstOrDefault(m => m.Success)?.Groups["url"].Value
            : null;
}
