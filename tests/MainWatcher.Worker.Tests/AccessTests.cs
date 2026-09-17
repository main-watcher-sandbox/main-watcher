using System.Net;
using System.Security.Cryptography;
using System.Text;
using MainWatcher.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace MainWatcher.Worker.Tests;

public class AccessTests
{
    static WorkerSettings Settings() => new("owner/watcher", 1, new("2", RSA.Create(2048)), new("3", RSA.Create(2048)),
        TimeSpan.FromSeconds(60), "targets.yml", GitHubApp.DefaultApi);

    static GitHubAccess Access(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(Settings(), NullLoggerFactory.Instance, new Handler(send), (_, _) => Task.CompletedTask);

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    public async Task ARejectedCredentialIsAConfigurationError(HttpStatusCode status, int code)
    {
        var error = await Assert.ThrowsAsync<WorkerConfigurationException>(() =>
            Access(_ => new HttpResponseMessage(status)).Verify(TestContext.Current.CancellationToken));
        Assert.Contains("mw-observer could not authenticate to owner/watcher", error.Message);
        Assert.Contains($"HTTP {code}", error.Message);
        Assert.Contains("installed on that repository", error.Message);
    }

    [Fact]
    public async Task ARejectedDoorbellKeyIsNamedToo()
    {
        var requests = new List<string>();
        var error = await Assert.ThrowsAsync<WorkerConfigurationException>(() => Access(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            // The observer's App ID is 2 and the doorbell's is 3; only the second App's key is refused.
            return requests.Count(r => r.EndsWith("/installation")) == 2
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Token(request);
        }).Verify(TestContext.Current.CancellationToken));
        Assert.Contains("mw-doorbell could not authenticate", error.Message);
    }

    [Fact]
    public async Task VerifyingMintsOneTokenPerApp()
    {
        var requests = new List<string>();
        await Access(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Token(request);
        }).Verify(TestContext.Current.CancellationToken);
        Assert.Equal(["GET /repos/owner/watcher/installation", "POST /app/installations/77/access_tokens",
            "GET /repos/owner/watcher/installation", "POST /app/installations/77/access_tokens"], requests);
    }

    [Fact]
    public async Task ATransientFailureOnlyLogsAndLetsTheCyclesRetry()
    {
        // A GitHub outage is not a configuration error: the worker starts, and its cycles try again.
        await Access(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)).Verify(TestContext.Current.CancellationToken);
        await Access(_ => throw new HttpRequestException("no such host")).Verify(TestContext.Current.CancellationToken);
    }

    static HttpResponseMessage Token(HttpRequestMessage request) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(request.Method == HttpMethod.Get
            ? "{\"id\":77}" : $"{{\"token\":\"ghs_x\",\"expires_at\":\"{DateTimeOffset.UtcNow.AddHours(1):O}\"}}",
            Encoding.UTF8, "application/json")
    };

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(send(request));
    }
}
