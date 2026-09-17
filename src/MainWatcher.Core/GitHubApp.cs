using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MainWatcher.Core;

/// <summary>An installation access token and when GitHub expires it.</summary>
public sealed record InstallationToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>GitHub App authentication for the trigger worker's <c>mw-observer</c> and <c>mw-doorbell</c> Apps (ADR-010).</summary>
public static class GitHubApp
{
    public static readonly Uri DefaultApi = new("https://api.github.com/");

    /// <summary>An HTTP client with GitHub's REST headers. The handler supplies authentication.</summary>
    public static HttpClient Client(Uri api, HttpMessageHandler handler, bool disposeHandler = true)
    {
        var http = new HttpClient(handler, disposeHandler) { BaseAddress = api, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MainWatcher/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    /// <summary>
    /// An RS256 JSON Web Token for the App. It is backdated 60 s for clock drift and lasts 9 minutes, under GitHub's 10-minute limit.
    /// </summary>
    public static string Jwt(string appId, RSA key, DateTimeOffset now)
    {
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = appId
        }));
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Encode(signature)}";
    }

    static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Authenticates each request as the App itself, for the installation endpoints.</summary>
public sealed class GitHubAppJwtHandler(string appId, RSA key, Func<DateTimeOffset>? clock = null) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GitHubApp.Jwt(appId, key, (clock ?? (() => DateTimeOffset.UtcNow))()));
        return base.SendAsync(request, ct);
    }
}

/// <summary>
/// Authenticates each request with an installation token from <paramref name="mint"/>, reused until 5 minutes before it expires.
/// </summary>
public sealed class InstallationTokenHandler(Func<CancellationToken, Task<InstallationToken>> mint, Func<DateTimeOffset>? clock = null)
    : DelegatingHandler
{
    readonly SemaphoreSlim gate = new(1, 1);
    InstallationToken? current;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token(ct));
        return await base.SendAsync(request, ct);
    }

    async Task<string> Token(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (current is null || current.ExpiresAt - (clock ?? (() => DateTimeOffset.UtcNow))() < TimeSpan.FromMinutes(5))
                current = await mint(ct);
            return current.Token;
        }
        finally { gate.Release(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) gate.Dispose();
        base.Dispose(disposing);
    }
}
