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

    [Theory]
    [InlineData("502", "Check 7: dispatch of main-watcher-tests.yml in owner/repo returned no run: HTTP 502 (")]
    [InlineData("network", "Check 7: dispatch of main-watcher-tests.yml in owner/repo returned no run: network error (connection reset)")]
    [InlineData("timeout", "Check 7: dispatch of main-watcher-tests.yml in owner/repo returned no run: timed out (")]
    [InlineData("empty", "Check 7: dispatch of main-watcher-tests.yml in owner/repo returned no run: the response carried no workflow_run_id.")]
    public async Task DispatchWithoutRunLogsWhyAndSendsOnce(string failure, string expected)
    {
        var requests = 0;
        using var http = Client(new Handler(_ =>
        {
            requests++;
            return failure switch
            {
                "502" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)),
                "network" => throw new HttpRequestException("connection reset"),
                "timeout" => throw new TaskCanceledException("The request timed out."),
                _ => Task.FromResult(Response("")),
            };
        }));
        var log = new List<string>();
        var gateway = new GitHubGateway(http, 1, (_, _) => Task.CompletedTask, log.Add);
        Assert.Null(await gateway.Dispatch("owner/repo", "head", 7, TestContext.Current.CancellationToken));
        Assert.Equal(1, requests);
        Assert.StartsWith(expected, Assert.Single(log));
    }

    [Fact]
    public async Task RejectedDispatchStillThrowsWithoutLogging()
    {
        using var http = Client(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity))));
        var log = new List<string>();
        var gateway = new GitHubGateway(http, 1, log: log.Add);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.Dispatch("owner/repo", "head", 7, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, error.StatusCode);
        Assert.Empty(log);
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

    [Fact]
    public async Task IssueCallsListOnlyIssuesCreateTheMissingLabelAndReadOnlyMissingFilesAsNull()
    {
        var requests = new List<string>();
        using var http = Client(new Handler(async request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            requests.Add($"{request.Method} {path} {(request.Content is null ? "" : await request.Content.ReadAsStringAsync())}");
            if (path.Contains("/contents/empty")) return Response("{\"type\":\"file\",\"encoding\":\"base64\",\"content\":\"\"}");
            if (path.Contains("/contents/owners"))
                return Response("{\"type\":\"file\",\"encoding\":\"base64\",\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes("* @team")) + "\\n\"}");
            if (path.Contains("/contents/")) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (path.EndsWith("/labels")) return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
            const string issue = """{"number":5,"title":"main is broken","body":"b","html_url":"https://github.com/owner/repo/issues/5","user":{"login":"main-watcher[bot]","type":"Bot"}}""";
            return Response(request.Method == HttpMethod.Get
                ? $$$"""[{{{issue}}},{"number":6,"title":"pr","html_url":"u","user":{"login":"x","type":"User"},"pull_request":{}}]"""
                : issue);
        }));
        var gateway = new GitHubGateway(http, 1);
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await gateway.File("owner/repo", "CODEOWNERS", ct));
        Assert.Equal("", await gateway.File("owner/repo", "empty", ct));
        Assert.Equal("* @team", await gateway.File("owner/repo", "owners", ct));
        var open = Assert.Single(await gateway.OpenIssues("owner/repo", "main-broken", ct));
        Assert.Equal(new Issue(5, "main is broken", "b", "main-watcher[bot]", "Bot", "https://github.com/owner/repo/issues/5"), open);
        Assert.Equal(5, (await gateway.CreateIssue("owner/repo", "t", "body", "main-broken", ct)).Number);
        await gateway.Close("owner/repo", 5, ct);
        Assert.Contains(requests, r => r.StartsWith("GET /repos/owner/repo/issues?state=open&labels=main-broken"));
        Assert.Contains(requests, r => r.StartsWith("POST /repos/owner/repo/issues ") && r.Contains("\"labels\":[\"main-broken\"]"));
        Assert.Contains(requests, r => r.StartsWith("PATCH /repos/owner/repo/issues/5 ") && r.Contains("\"state\":\"closed\""));
    }

    [Fact]
    public async Task ActivityAndHistoryStopAtTheLimitWithoutReadingAnotherPage()
    {
        var requests = new List<string>();
        using var http = Client(new Handler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            requests.Add(path);
            var body = path.Contains("/activity?")
                ? "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => i == 0
                    ? """{"before":"b","after":"a","timestamp":"2026-09-16T20:31:54Z","activity_type":"merge_queue_merge","actor":{"login":"github-merge-queue[bot]"}}"""
                    : """{"before":"b","after":"a","timestamp":"2026-09-16T20:00:00Z","activity_type":"push","actor":null}""")) + "]"
                : "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $"{{\"sha\":\"c{i}\"}}")) + "]";
            var response = Response(body);
            response.Headers.Add("Link", $"<https://api.github.com{request.RequestUri.AbsolutePath}?after=cursor>; rel=\"next\"");
            return Task.FromResult(response);
        }));
        var gateway = new GitHubGateway(http, 1);
        var ct = TestContext.Current.CancellationToken;
        var pushes = await gateway.Pushes("owner/repo", 100, ct);
        Assert.Equal(100, pushes.Count);
        Assert.Equal(new Push("b", "a", DateTimeOffset.Parse("2026-09-16T20:31:54Z"), "merge_queue_merge", "github-merge-queue[bot]"), pushes[0]);
        Assert.Null(pushes[1].Actor);
        Assert.Equal(new[] { "c0", "c1" }, await gateway.History("owner/repo", "head", 2, ct));
        Assert.Equal(2, requests.Count);
        Assert.StartsWith("/repos/owner/repo/activity?ref=main&direction=desc&per_page=100", requests[0]);
        Assert.StartsWith("/repos/owner/repo/commits?sha=head&per_page=100", requests[1]);
    }

    [Fact]
    public async Task CommitCountComparesOneCommitPageAndIsUnknownForUnreachableOrMissingCommits()
    {
        string a = new('a', 40), b = new('b', 40), gone = new('9', 40), zero = new('0', 40);
        var requests = new List<string>();
        using var http = Client(new Handler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(request.RequestUri.AbsolutePath.Contains(gone)
                ? new HttpResponseMessage(HttpStatusCode.NotFound) : Response("{\"total_commits\":4,\"commits\":[{}]}"));
        }));
        var gateway = new GitHubGateway(http, 1);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(4, await gateway.CommitCount("owner/repo", a, b, ct));
        Assert.Null(await gateway.CommitCount("owner/repo", gone, b, ct));
        Assert.Null(await gateway.CommitCount("owner/repo", zero, b, ct));
        Assert.Equal(new[] { $"/repos/owner/repo/compare/{a}...{b}?per_page=1", $"/repos/owner/repo/compare/{gone}...{b}?per_page=1" }, requests);
    }

    static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://api.github.com/") };
    static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}


