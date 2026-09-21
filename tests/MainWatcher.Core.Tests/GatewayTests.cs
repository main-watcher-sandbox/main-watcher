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

    // A bare "403 (Forbidden)" hid whether a permission or a rate limit refused every cycle (#25).
    [Fact]
    public async Task ARefusedRequestSaysWhatAndWhyWithTheRateLimit()
    {
        using var http = Client(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"message\":\"You have exceeded a secondary rate limit.\"}", Encoding.UTF8, "application/json")
            };
            response.Headers.Add("x-ratelimit-remaining", "4211");
            response.Headers.Add("retry-after", "60");
            return Task.FromResult(response);
        }));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GitHubGateway(http, 1, (_, _) => Task.CompletedTask).Jobs("owner/repo", 1, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, e.StatusCode);
        Assert.Equal("GitHub answered 403 (Forbidden) to GET /repos/owner/repo/actions/runs/1/jobs: You have exceeded a secondary rate limit. "
            + "[x-ratelimit-remaining: 4211, retry-after: 60]", e.Message);
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
                {"check_runs":[{"id":1,"head_sha":"old","status":"completed","conclusion":"neutral","started_at":"2026-09-16T18:00:00Z","app":{"id":7},"output":{"title":"Infrastructure error"}}]}
                """));
            return Task.FromResult(Response("{\"check_runs\":[]}"));
        }));
        var checks = await new GitHubGateway(http, 7).Checks("owner/repo", TestContext.Current.CancellationToken);
        Assert.Equal(("old", "Infrastructure error"), (Assert.Single(checks).Sha, checks[0].Title));
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
        await gateway.Close("owner/repo", 5, "completed", null, ct);
        Assert.Contains(requests, r => r.StartsWith("GET /repos/owner/repo/issues?state=open&labels=main-broken"));
        Assert.Contains(requests, r => r.StartsWith("POST /repos/owner/repo/issues ") && r.Contains("\"labels\":[\"main-broken\"]"));
        Assert.Contains(requests, r => r.StartsWith("PATCH /repos/owner/repo/issues/5 ") && r.Contains("\"state\":\"closed\""));
    }

    [Fact]
    public async Task LockReadsListEveryStateSinceTheLookbackAndReadWhoClosedFromTheSingleIssue()
    {
        var requests = new List<string>();
        using var http = Client(new Handler(async request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            requests.Add($"{request.Method} {path} {(request.Content is null ? "" : await request.Content.ReadAsStringAsync())}");
            if (request.Method == HttpMethod.Patch) return Response("{}");
            if (path.Contains("/comments")) return Response("""[{"body":"x","user":{"login":"main-watcher[bot]","type":"Bot"}},{"body":"y","user":null}]""");
            if (path.Contains("/issues?")) return Response("""[{"number":5,"title":"t","body":"b","html_url":"u","state":"closed","state_reason":"duplicate","updated_at":"2026-09-16T18:00:00Z","id":5488651743,"user":{"login":"main-watcher[bot]","type":"Bot"}}]""");
            return Response(path.EndsWith("/5")
                ? """{"number":5,"closed_by":{"login":"alice","type":"User"},"user":{"login":"main-watcher[bot]","type":"Bot"}}"""
                : """{"number":6,"closed_by":null,"user":{"login":"main-watcher[bot]","type":"Bot"}}""");
        }));
        var gateway = new GitHubGateway(http, 1);
        var ct = TestContext.Current.CancellationToken;
        var issue = Assert.Single(await gateway.Issues("owner/repo", "main-broken", DateTimeOffset.Parse("2026-08-17T19:00:00Z"), ct));
        Assert.Equal(new Issue(5, "t", "b", "main-watcher[bot]", "Bot", "u", "closed", "duplicate", DateTimeOffset.Parse("2026-09-16T18:00:00Z"), 5488651743), issue);
        Assert.Equal(new IssueComment[] { new("x", "main-watcher[bot]", "Bot"), new("y", "", "") }, await gateway.Comments("owner/repo", 5, null, ct));
        Assert.Equal(new Account("alice", "User"), await gateway.ClosedBy("owner/repo", 5, ct));
        Assert.Null(await gateway.ClosedBy("owner/repo", 6, ct));
        await gateway.EditBody("owner/repo", 5, "new", ct);
        await gateway.Close("owner/repo", 5, "duplicate", 5488651743, ct);
        Assert.StartsWith("GET /repos/owner/repo/issues?state=all&labels=main-broken&since=2026-08-17T19%3A00%3A00Z&per_page=100", requests[0]);
        Assert.StartsWith("GET /repos/owner/repo/issues/5/comments?per_page=100", requests[1]);
        Assert.Equal("PATCH /repos/owner/repo/issues/5 {\"body\":\"new\"}", requests[4]);
        Assert.Equal("PATCH /repos/owner/repo/issues/5 {\"state\":\"closed\",\"state_reason\":\"duplicate\",\"duplicate_issue_id\":5488651743}", requests[5]);
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

    [Fact]
    public async Task DispatchWorkflowPostsInputsOnMainOnce()
    {
        var requests = new List<string>();
        using var http = Client(new Handler(async request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new GitHubGateway(http, 1, (_, _) => Task.CompletedTask)
            .DispatchWorkflow("owner/watcher", "watch.yml", new Dictionary<string, string> { ["target"] = "owner/repo" }, TestContext.Current.CancellationToken));
        // A POST is never retried: a lost response may still have started the run.
        Assert.Equal(["POST /repos/owner/watcher/actions/workflows/watch.yml/dispatches {\"ref\":\"main\",\"inputs\":{\"target\":\"owner/repo\"}}"], requests);
    }

    [Fact]
    public void AppJwtIsSignedByTheAppKeyAndExpiresWithinTenMinutes()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var now = DateTimeOffset.Parse("2026-09-16T19:00:00Z");
        var parts = GitHubApp.Jwt("4966600", key, now).Split('.');
        static byte[] Decode(string part) => Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/').PadRight((part.Length + 3) / 4 * 4, '='));
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Decode(parts[2]),
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
        Assert.Equal("RS256", JsonNode.Parse(Decode(parts[0]))!["alg"]!.GetValue<string>());
        var payload = JsonNode.Parse(Decode(parts[1]))!;
        Assert.Equal("4966600", payload["iss"]!.GetValue<string>());
        Assert.Equal(now.AddSeconds(-60).ToUnixTimeSeconds(), payload["iat"]!.GetValue<long>());
        Assert.Equal(now.AddMinutes(9).ToUnixTimeSeconds(), payload["exp"]!.GetValue<long>());
    }

    [Fact]
    public async Task InstallationTokensAreScopedToOneRepositoryAndReusedUntilNearExpiry()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.Parse("2026-09-16T19:00:00Z");
        var requests = new List<string>();
        var minted = 0;
        using var key = System.Security.Cryptography.RSA.Create(2048);
        using var app = new HttpClient(new GitHubAppJwtHandler("2", key, () => now) { InnerHandler = new Handler(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(3, request.Headers.Authorization.Parameter!.Split('.').Length);
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct))}".TrimEnd());
            return Response(request.Method == HttpMethod.Get ? "{\"id\":77}"
                : $"{{\"token\":\"ghs_{++minted}\",\"expires_at\":\"{now.AddHours(1):O}\"}}");
        }) }) { BaseAddress = new Uri("https://api.github.com/") };
        var appGateway = new GitHubGateway(app, 1);
        var used = new List<string>();
        using var http = new HttpClient(new InstallationTokenHandler(t => appGateway.InstallationToken("owner/repo", t), () => now)
        {
            InnerHandler = new Handler(request =>
            {
                used.Add(request.Headers.Authorization!.ToString());
                return Task.FromResult(Response("{\"sha\":\"head\"}"));
            })
        }) { BaseAddress = new Uri("https://api.github.com/") };
        var gateway = new GitHubGateway(http, 1);

        await gateway.MainHead("owner/repo", ct);
        now = now.AddMinutes(54);
        await gateway.MainHead("owner/repo", ct);
        now = now.AddMinutes(2);
        await gateway.MainHead("owner/repo", ct);

        Assert.Equal(["Bearer ghs_1", "Bearer ghs_1", "Bearer ghs_2"], used);
        Assert.Equal(["GET /repos/owner/repo/installation", "POST /app/installations/77/access_tokens {\"repositories\":[\"repo\"]}"], requests.Take(2));
        Assert.Equal(4, requests.Count);
    }

    // ADR-008: the sweep reads the target's merge-group gate runs and keeps the fail-open check runs posted in its window.
    // Run 9's job was skipped, so its gate enforced the lock; run 7's fail-open was posted before the window, and run 8's after
    // it, although run 8 itself started earlier: the job's own time is what "posted in the past hour" means.
    [Fact]
    public async Task FailOpensKeepsTheFailOpenCheckRunsPostedInTheWindow()
    {
        var paths = new List<string>();
        var started = new Dictionary<string, string>
        {
            ["/9/"] = "2026-09-17T10:20:00Z", ["/8/"] = "2026-09-17T10:18:00Z", ["/7/"] = "2026-09-17T10:16:00Z"
        };
        var handler = new Handler(request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath.EndsWith("/jobs"))
            {
                var run = started.Keys.First(k => request.RequestUri.AbsolutePath.Contains(k));
                return Task.FromResult(Response(System.Text.Json.JsonSerializer.Serialize(new
                {
                    jobs = new object[]
                    {
                        new { name = "main-watcher-gate", conclusion = "success", started_at = "2026-09-17T10:15:00Z" },
                        new { name = "main-watcher/gate-fail-open", conclusion = run == "/9/" ? "skipped" : "success", started_at = started[run] }
                    }
                })));
            }
            return Task.FromResult(Response(System.Text.Json.JsonSerializer.Serialize(new
            {
                workflow_runs = new[]
                {
                    new { id = 9L, head_sha = "aaa", head_branch = "gh-readonly-queue/main/pr-1", created_at = "2026-09-17T10:14:00Z" },
                    new { id = 8L, head_sha = "bbb", head_branch = "gh-readonly-queue/main/pr-2", created_at = "2026-09-17T10:14:30Z" },
                    new { id = 7L, head_sha = "ccc", head_branch = "gh-readonly-queue/main/pr-3", created_at = "2026-09-17T10:15:30Z" }
                }
            })));
        });
        using var http = Client(handler);
        var since = DateTimeOffset.Parse("2026-09-17T10:17:00Z");
        var found = await new GitHubGateway(http, 1).FailOpens("owner/repo", since, TestContext.Current.CancellationToken);
        var open = Assert.Single(found);
        Assert.Equal(8, open.RunId);
        Assert.Equal("gh-readonly-queue/main/pr-2", open.Branch);
        // The check run's own time, so the next sweep's window starts where this one ended.
        Assert.Equal(DateTimeOffset.Parse("2026-09-17T10:18:00Z"), open.At);
        Assert.Contains("workflows/main-watcher-gate.yml/runs?event=merge_group", paths[0]);
        // Runs from before the window are read too: the fail-open job appears only after the gate job has finished.
        Assert.Contains("created=%3E%3D2026-09-17T09%3A17", paths[0]);
    }

    [Fact]
    public async Task ATargetWithoutTheGateWorkflowHasNoFailOpens()
    {
        using var http = Client(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Empty(await new GitHubGateway(http, 1).FailOpens("owner/repo", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
    }

    // ADR-015: a merge-queue entry brings in its own pull request and everything ahead of it, so the whole range is read. The
    // pull request comes from the commit subject, not from the commits-to-pull-requests API the App cannot call (§8).
    [Fact]
    public async Task MergedCommitsNamesThePullRequestOfEachSubjectItRecognises()
    {
        var path = "";
        var handler = new Handler(request =>
        {
            path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(Response("{\"total_commits\":3,\"commits\":["
                + "{\"sha\":\"c1\",\"commit\":{\"message\":\"Add a feature (#8)\\n\\nbody\",\"committer\":{\"date\":\"2026-09-17T09:00:00Z\"}}},"
                + "{\"sha\":\"c2\",\"commit\":{\"message\":\"a commit of its own\",\"committer\":{\"date\":\"2026-09-17T09:30:00Z\"}}},"
                + "{\"sha\":\"c3\",\"commit\":{\"message\":\"Merge pull request #7 from owner/fix\",\"committer\":{\"date\":\"2026-09-17T10:00:00Z\"}}}]}"));
        });
        using var http = Client(handler);
        var range = await new GitHubGateway(http, 1).MergedCommits("owner/repo", new('a', 40), new('b', 40), 0,
            TestContext.Current.CancellationToken);
        Assert.NotNull(range);
        Assert.False(range.Truncated);
        var commits = range.Commits;
        Assert.Equal([8, null, 7], commits.Select(c => c.Pull));
        Assert.Equal("a commit of its own", commits[1].Subject);
        Assert.Equal(DateTimeOffset.Parse("2026-09-17T10:00:00Z"), commits[2].At);
        Assert.Equal([(8, DateTimeOffset.Parse("2026-09-17T09:00:00Z")), (7, DateTimeOffset.Parse("2026-09-17T10:00:00Z"))],
            Reconciliation.Pulls(commits));
        Assert.Contains("/compare/", path);
    }

    // PR #55 review: a range longer than one pass reads is finished by the next, so the read starts where the last stopped.
    [Theory]
    // From the start: two pages read, and the third page's absence ends it.
    [InlineData(0, new[] { 1, 2, 3 }, 250, 250, false)]
    // Resumed inside the second page: it starts there and drops the fifty commits already read.
    [InlineData(150, new[] { 2, 3 }, 100, 250, false)]
    // Resumed with the last commits left: nothing beyond them remains.
    [InlineData(240, new[] { 3 }, 10, 250, false)]
    // A range longer than one pass's budget stops at it and says so.
    [InlineData(0, new[] { 1, 2, 3, 4, 5 }, 500, 900, true)]
    public async Task ALongRangeIsReadFromWhereTheLastPassStopped(int skip, int[] pages, int expected, int total, bool truncated)
    {
        var read = new List<int>();
        var handler = new Handler(request =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var page = int.Parse(query["page"]!);
            read.Add(page);
            // 100 commits a page until the range runs out, each naming nothing, so only the arithmetic is under test.
            var on = Math.Clamp(total - (page - 1) * 100, 0, 100);
            var commits = string.Join(",", Enumerable.Range(0, on).Select(i =>
                $"{{\"sha\":\"c{(page - 1) * 100 + i}\",\"commit\":{{\"message\":\"a commit\",\"committer\":{{\"date\":\"2026-09-17T09:00:00Z\"}}}}}}"));
            return Task.FromResult(Response($"{{\"total_commits\":{total},\"commits\":[{commits}]}}"));
        });
        using var http = Client(handler);
        var range = await new GitHubGateway(http, 1).MergedCommits("owner/repo", new('a', 40), new('b', 40), skip,
            TestContext.Current.CancellationToken);
        Assert.NotNull(range);
        Assert.Equal(pages, read);
        Assert.Equal(expected, range.Commits.Count);
        Assert.Equal(truncated, range.Truncated);
        // The first commit read is the one after the last pass's, whichever page it fell in.
        Assert.Equal($"c{skip}", range.Commits[0].Sha);
    }

    [Fact]
    public async Task AnUncomparableOrUnusableRangeCompletesNoComparison()
    {
        var calls = 0;
        var handler = new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var http = Client(handler);
        var gateway = new GitHubGateway(http, 1);
        var ct = TestContext.Current.CancellationToken;
        // A force push can leave "before" unreachable, and the activity's first entry has no "before" at all.
        Assert.Null(await gateway.MergedCommits("owner/repo", new('a', 40), new('b', 40), 0, ct));
        Assert.Equal(1, calls);
        Assert.Null(await gateway.MergedCommits("owner/repo", new('0', 40), new('b', 40), 0, ct));
        Assert.Equal(1, calls);
    }

    // ADR-015 judges the labels at the merge, and the merge is in this same timeline, so one read gives both.
    [Fact]
    public async Task PullEventsKeepsOnlyLabelChangesAndTheMergeInTheOrderGitHubReturnsThem()
    {
        var handler = new Handler(request =>
        {
            Assert.EndsWith("/issues/7/events", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Response(
                "[{\"event\":\"labeled\",\"label\":{\"name\":\"fixes-main\"},\"created_at\":\"2026-09-17T10:00:00Z\"},"
                + "{\"event\":\"closed\",\"created_at\":\"2026-09-17T10:01:00Z\"},"
                + "{\"event\":\"unlabeled\",\"label\":{\"name\":\"fixes-main\"},\"created_at\":\"2026-09-17T10:02:00Z\"},"
                + "{\"event\":\"labeled\",\"created_at\":\"2026-09-17T10:03:00Z\"},"
                + "{\"event\":\"merged\",\"commit_id\":\"abc\",\"created_at\":\"2026-09-17T10:04:00Z\"}]"));
        });
        using var http = Client(handler);
        var events = await new GitHubGateway(http, 1).PullEvents("owner/repo", 7, TestContext.Current.CancellationToken);
        // A label event without a label, and every other kind of event, is dropped.
        Assert.Equal(["labeled", "unlabeled", "merged"], events.Select(e => e.Name));
        Assert.Equal("fixes-main", events[1].Label);
        Assert.Equal(DateTimeOffset.Parse("2026-09-17T10:02:00Z"), events[1].At);
        Assert.Equal(DateTimeOffset.Parse("2026-09-17T10:04:00Z"), Reconciliation.MergedAt(events));
        // The label was taken off before the merge, so it was not a fix when it merged.
        Assert.False(Reconciliation.WasFix(events, Reconciliation.MergedAt(events)!.Value));
    }

    // R-7: the queue branch names the entry the queue removed when its gate failed.
    [Fact]
    public async Task GateBlocksNamesThePullRequestOfEachFailedMergeGroup()
    {
        var query = "";
        var handler = new Handler(request =>
        {
            query = request.RequestUri!.PathAndQuery;
            return Task.FromResult(Response("{\"workflow_runs\":[" +
                $"{{\"id\":9,\"head_branch\":\"gh-readonly-queue/main/pr-12-{new string('a', 40)}\",\"run_started_at\":\"2026-09-17T10:20:00Z\"}}," +
                "{\"id\":8,\"head_branch\":\"main\",\"run_started_at\":\"2026-09-17T10:10:00Z\"}," +
                $"{{\"id\":7,\"head_branch\":\"gh-readonly-queue/main/pr-3-{new string('b', 40)}\",\"created_at\":\"2026-09-17T10:00:00Z\"}}]}}"));
        });
        using var http = Client(handler);
        var blocked = await new GitHubGateway(http, 1).GateBlocks("owner/repo", DateTimeOffset.Parse("2026-09-17T09:00:00Z"),
            TestContext.Current.CancellationToken);
        // Oldest first, and a run that is not a merge-queue entry names no pull request.
        Assert.Equal([3, 12], blocked.Select(b => b.Pull));
        Assert.Equal(7, blocked[0].RunId);
        Assert.Contains("event=merge_group&status=failure", query);
    }

    // ADR-016: a group the queue sweep removed was blocked by a re-run of a gate run GitHub still dates by its first attempt,
    // so the read reaches back past the lock and the window is judged on the attempt that failed.
    [Fact]
    public async Task GateBlocksCountsTheAttemptThatFailedAndNamesTheOnesTheSweepReran()
    {
        var query = "";
        var handler = new Handler(request =>
        {
            query = request.RequestUri!.PathAndQuery;
            return Task.FromResult(Response("{\"workflow_runs\":[" +
                // Queued an hour before the lock, re-run by the sweep and failed inside its window.
                $"{{\"id\":9,\"head_branch\":\"gh-readonly-queue/main/pr-12-{new string('a', 40)}\",\"run_attempt\":2," +
                "\"created_at\":\"2026-09-17T08:00:00Z\",\"run_started_at\":\"2026-09-17T10:20:00Z\"}," +
                // A group the gate failed before this lock existed: not this lock's doing, however recently it was read.
                $"{{\"id\":7,\"head_branch\":\"gh-readonly-queue/main/pr-3-{new string('b', 40)}\"," +
                "\"created_at\":\"2026-09-17T08:30:00Z\",\"run_started_at\":\"2026-09-17T08:30:00Z\"}]}"));
        });
        using var http = Client(handler);
        var blocked = await new GitHubGateway(http, 1).GateBlocks("owner/repo", DateTimeOffset.Parse("2026-09-17T09:00:00Z"),
            TestContext.Current.CancellationToken);
        var group = Assert.Single(blocked);
        Assert.Equal(12, group.Pull);
        Assert.Equal(2, group.Attempt);
        Assert.Contains("created=%3E%3D2026-09-16T09%3A00", query);
    }

    // ADR-016: the groups still in the queue, from the branches GitHub makes for them (A-7).
    [Fact]
    public async Task QueuedGroupsReadsTheMergeQueueBranchesAndNamesTheirPullRequests()
    {
        var path = "";
        var handler = new Handler(request =>
        {
            path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(Response("[" +
                $"{{\"ref\":\"refs/heads/gh-readonly-queue/main/pr-12-{new string('a', 40)}\",\"object\":{{\"sha\":\"{new string('c', 40)}\"}}}}," +
                // A branch under the prefix that is not a queue entry still has a commit its gate ran on.
                "{\"ref\":\"refs/heads/gh-readonly-queue/main/other\",\"object\":{\"sha\":\"" + new string('d', 40) + "\"}}]"));
        });
        using var http = Client(handler);
        var groups = await new GitHubGateway(http, 1).QueuedGroups("owner/repo", TestContext.Current.CancellationToken);
        Assert.Equal("/repos/owner/repo/git/matching-refs/heads/gh-readonly-queue/main/", path);
        Assert.Equal([12, null], groups.Select(g => g.Pull));
        Assert.Equal($"gh-readonly-queue/main/pr-12-{new string('a', 40)}", groups[0].Branch);
        Assert.Equal(new string('c', 40), groups[0].Sha);
    }

    // The gate runs on one group's commit, dated by the latest attempt: a run this sweep re-ran is not re-run again.
    [Fact]
    public async Task GateRunsAreDatedByTheirLatestAttempt()
    {
        var query = "";
        var handler = new Handler(request =>
        {
            query = request.RequestUri!.PathAndQuery;
            return Task.FromResult(Response("{\"workflow_runs\":["
                + "{\"id\":9,\"status\":\"in_progress\",\"conclusion\":null,\"created_at\":\"2026-09-17T08:00:00Z\","
                + "\"run_started_at\":\"2026-09-17T10:20:00Z\"}]}"));
        });
        using var http = Client(handler);
        var run = Assert.Single(await new GitHubGateway(http, 1).GateRuns("owner/repo", "abc", TestContext.Current.CancellationToken));
        Assert.Equal((9L, "in_progress", (string?)null, DateTimeOffset.Parse("2026-09-17T10:20:00Z")), (run.Id, run.Status, run.Conclusion, run.StartedAt));
        Assert.Contains("event=merge_group&head_sha=abc", query);
    }

    // A re-run GitHub will not accept is an answer, not an exception: the sweep stays owed and says why.
    [Theory]
    [InlineData(HttpStatusCode.OK, null)]
    [InlineData(HttpStatusCode.Forbidden, "HTTP 403")]
    public async Task ARefusedRerunIsReportedRatherThanThrown(HttpStatusCode status, string? refusal)
    {
        var path = "";
        using var http = Client(new Handler(request =>
        {
            path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(status == HttpStatusCode.OK ? Response("") : new HttpResponseMessage(status));
        }));
        Assert.Equal(refusal, await new GitHubGateway(http, 1).Rerun("owner/repo", 42, TestContext.Current.CancellationToken));
        Assert.Equal("/repos/owner/repo/actions/runs/42/rerun", path);
    }

    [Fact]
    public async Task ATargetWithoutTheGateWorkflowBlocksNothing()
    {
        using var http = Client(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Empty(await new GitHubGateway(http, 1).GateBlocks("owner/repo", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
    }

    // ADR-015: the window a lock's reconciliation covers comes from these two fields.
    [Fact]
    public async Task IssuesCarryTheirCreationAndClosureTimes()
    {
        using var http = Client(new Handler(_ => Task.FromResult(Response(
            "[{\"number\":1,\"title\":\"main is broken\",\"body\":\"\",\"user\":{\"login\":\"main-watcher[bot]\",\"type\":\"Bot\"},"
            + "\"state\":\"closed\",\"created_at\":\"2026-09-17T08:00:00Z\",\"closed_at\":\"2026-09-17T10:00:00Z\"}]"))));
        var issue = Assert.Single(await new GitHubGateway(http, 1).Issues("owner/repo", "main-broken",
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"), TestContext.Current.CancellationToken));
        Assert.Equal(DateTimeOffset.Parse("2026-09-17T08:00:00Z"), issue.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-17T10:00:00Z"), issue.ClosedAt);
    }

    static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://api.github.com/") };
    static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}


