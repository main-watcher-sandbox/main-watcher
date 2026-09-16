using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MainWatcher.Core;

namespace MainWatcher.Core.Tests;

public class GatewayTests
{
    [Theory]
    [InlineData("{\"workflow_run_id\":42}", 42L)]
    [InlineData("", null)]
    public async Task DispatchRequestsRunDetailsAndPassesInputs(string response, long? expected)
    {
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("main-watcher-tests.yml/dispatches", request.RequestUri!.AbsolutePath);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
            Assert.True(body["return_run_details"]!.GetValue<bool>());
            Assert.Equal("main", body["ref"]!.GetValue<string>());
            Assert.Equal("test-sha", body["inputs"]!["sha"]!.GetValue<string>());
            Assert.Equal("7", body["inputs"]!["check_run_id"]!.GetValue<string>());
            return Response(response);
        });
        using var http = Client(handler);
        Assert.Equal(expected, await new GitHubGateway(http, 1).Dispatch("owner/repo", "test-sha", 7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadsEveryJobsPageAndIgnoresOtherRunningJobs()
    {
        var handler = new Handler(request =>
        {
            var jobs = request.RequestUri!.Query.EndsWith("&page=1")
                ? Enumerable.Range(0, 100).Select(i => new { name = $"other-{i}", status = "in_progress", steps = Array.Empty<object>() }).Cast<object>().ToArray()
                : [new { name = "tests / main-watcher", status = "completed", steps = new[] { new { name = "main-watcher-test", conclusion = "failure" }, new { name = "main-watcher-tests-finished", conclusion = "success" } } }];
            var response = Response(System.Text.Json.JsonSerializer.Serialize(new { jobs }));
            if (request.RequestUri.Query.EndsWith("&page=1")) response.Headers.Add("Link", "<https://api.github.com/repos/owner/repo/actions/runs/42/jobs?filter=latest&per_page=100&page=2>; rel=\"next\"");
            return Task.FromResult(response);
        });
        using var http = Client(handler);
        var jobs = await new GitHubGateway(http, 1).Jobs("owner/repo", 42, TestContext.Current.CancellationToken);
        Assert.Equal(101, jobs!.Count);
        Assert.Equal("failure", Outcomes.Read(jobs)!.Conclusion);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task OnlyDeletedRunsBecomeUnknown(HttpStatusCode status, bool deleted)
    {
        using var http = Client(new Handler(_ => Task.FromResult(new HttpResponseMessage(status))));
        var gateway = new GitHubGateway(http, 1, (_, _) => Task.CompletedTask);
        if (deleted) Assert.Null(await gateway.Jobs("owner/repo", 1, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<HttpRequestException>(() => gateway.Jobs("owner/repo", 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UndownloadableArtifactIsUnknown()
    {
        using var http = Client(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        Assert.False((await new GitHubGateway(http, 1).Reports("owner/repo", 1, TestContext.Current.CancellationToken)).Known);
    }

    [Theory]
    [InlineData("completed", "neutral")]
    [InlineData("queued", null)]
    public async Task MissingJobWaitsOnlyWhileRunIsUnfinished(string status, string? conclusion)
    {
        using var http = Client(new Handler(request => Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("/jobs")
            ? "{\"jobs\":[]}" : $"{{\"status\":\"{status}\"}}"))));
        var jobs = await new GitHubGateway(http, 1).Jobs("owner/repo", 1, TestContext.Current.CancellationToken);
        Assert.Equal(conclusion, Outcomes.Read(jobs)?.Conclusion);
    }

    [Fact]
    public async Task RepeatedPollsRefreshPendingChecksWithoutWalkingHistoryAgain()
    {
        var requests = new List<string>();
        using var http = Client(new Handler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            requests.Add(path);
            if (path.EndsWith("/commits/main")) return Task.FromResult(Response("{\"sha\":\"head\"}"));
            if (path.Contains("/commits?")) return Task.FromResult(Response("[{\"sha\":\"head\"},{\"sha\":\"old\"},{\"sha\":\"ancient\"}]"));
            if (path.Contains("/old/check-runs")) return Task.FromResult(Response("""
                {"check_runs":[{"id":1,"head_sha":"old","status":"in_progress","conclusion":null,"started_at":"2026-09-16T18:00:00Z","app":{"id":7}}]}
                """));
            return Task.FromResult(Response("{\"check_runs\":[]}"));
        }));
        var gateway = new GitHubGateway(http, 7);
        await gateway.Checks("owner/repo", TestContext.Current.CancellationToken);
        requests.Clear();
        var next = await gateway.Checks("owner/repo", TestContext.Current.CancellationToken);
        Assert.Equal(3, requests.Count); // Head SHA, head checks, pending old-head checks.
        Assert.DoesNotContain(requests, path => path.Contains("/commits?"));
        Assert.DoesNotContain(requests, path => path.Contains("ancient"));
        Assert.Equal("in_progress", Assert.Single(next).Status);
    }

    [Fact]
    public async Task TransientReadsRetryButDispatchNeverRetries()
    {
        var requests = 0;
        using var http = Client(new Handler(_ => Task.FromResult(++requests <= 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response("{\"sha\":\"head\"}"))));
        var gateway = new GitHubGateway(http, 1, (_, _) => Task.CompletedTask);
        Assert.Equal("head", await gateway.MainHead("owner/repo", TestContext.Current.CancellationToken));
        Assert.Equal(3, requests);
        requests = 0;
        Assert.Null(await gateway.Dispatch("owner/repo", "head", 1, TestContext.Current.CancellationToken));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CommitHistoryFollowsNextLinkEvenWhenPageIsShort()
    {
        var requests = new List<string>();
        using var http = Client(new Handler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            requests.Add(path);
            if (path.EndsWith("/commits/main")) return Task.FromResult(Response("{\"sha\":\"head\"}"));
            if (path.Contains("/commits?") && path.Contains("cursor=older"))
                return Task.FromResult(Response("[{\"sha\":\"old\"}]"));
            if (path.Contains("/commits?"))
            {
                var response = Response("[{\"sha\":\"head\"}]");
                response.Headers.Add("Link", "<https://api.github.com/repos/owner/repo/commits?sha=head&cursor=older>; rel=\"next\"");
                return Task.FromResult(response);
            }
            if (path.Contains("/old/check-runs")) return Task.FromResult(Response("""
                {"check_runs":[{"id":1,"head_sha":"old","status":"in_progress","conclusion":null,"started_at":"2026-09-16T18:00:00Z","app":{"id":7}}]}
                """));
            return Task.FromResult(Response("{\"check_runs\":[]}"));
        }));
        var checks = await new GitHubGateway(http, 7).Checks("owner/repo", TestContext.Current.CancellationToken);
        Assert.Equal("old", Assert.Single(checks).Sha);
        Assert.Contains(requests, path => path.Contains("cursor=older"));
    }

    static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://api.github.com/") };
    static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}


