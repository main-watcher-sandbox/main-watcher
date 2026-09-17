using System.Text.Json;
using System.Text.Json.Nodes;
using MainWatcher.Core;

namespace MainWatcher.Core.Tests;

public class WatcherTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T19:00:00Z");
    static CheckRun Check(string status = "completed", string? conclusion = "success", string sha = "head") =>
        new(1, sha, status, conclusion, Now.AddHours(-1), Now.AddMinutes(-30), "42");
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void MergesRealXunitProjectsAndListsFailures()
    {
        var result = CtrfReader.Read([Fixture("project-0.json"), Fixture("project-1.json"), Fixture("failed-project.json")]);
        Assert.True(result.Known);
        Assert.Contains(result.Failures, f => f.Name.Contains("Alpha") && f.Suite.Length > 0);
    }

    [Fact]
    public void MissingOrAnyInvalidReportMakesFailuresUnknown()
    {
        Assert.False(CtrfReader.Read([]).Known);
        Assert.False(CtrfReader.Read([Fixture("project-0.json"), "{"]).Known);
        Assert.False(CtrfReader.Read(["{\"results\":{\"tests\":[]}}"]).Known);
        var invalid = JsonNode.Parse(Fixture("failed-project.json"))!;
        invalid["results"]!["tests"]![0]!["duration"] = "wrong type";
        Assert.False(CtrfReader.Read([invalid.ToJsonString()]).Known);
    }

    [Fact]
    public void FailureMessagesUseFirstLineAndTwoHundredCharacters()
    {
        var json = JsonNode.Parse(Fixture("failed-project.json"))!;
        var failed = json["results"]!["tests"]!.AsArray().First(t => t!["status"]!.GetValue<string>() == "failed")!;
        failed["message"] = new string('x', 230) + "\r\nsecond line";
        var result = CtrfReader.Read([json.ToJsonString()]);
        Assert.True(result.Known);
        Assert.Equal(new string('x', 200), result.Failures[0].Message);
    }

    [Theory]
    [InlineData("success", false)]
    [InlineData("failure", false)]
    [InlineData("neutral", true)]
    public void EligibilityUsesNewestHeadResult(string conclusion, bool expected) =>
        Assert.Equal(expected, Eligibility.CanStart("head", [Check(conclusion: conclusion)], TimeSpan.FromMinutes(15), Now));

    [Fact]
    public void EligibilityBlocksOlderActiveRunsAndHonorsIntervalAndCap()
    {
        Assert.False(Eligibility.CanStart("new", [Check("in_progress", null, "old")], TimeSpan.FromMinutes(15), Now, true));
        Assert.False(Eligibility.CanStart("new", [Check() with { StartedAt = Now.AddMinutes(-1) }], TimeSpan.FromMinutes(15), Now, true));
        var neutral = Check(conclusion: "neutral");
        Assert.False(Eligibility.CanStart("head", [neutral with { CompletedAt = Now.AddMinutes(-1) }], TimeSpan.FromMinutes(15), Now, true));
        Assert.False(Eligibility.CanStart("head", [neutral, neutral, neutral], TimeSpan.FromMinutes(15), Now));
        Assert.True(Eligibility.CanStart("head", [neutral, neutral, neutral], TimeSpan.FromMinutes(15), Now, true));
        Assert.True(Eligibility.CanStart("new", [Check()], TimeSpan.FromMinutes(15), Now));
    }

    [Theory]
    [InlineData("success", "success", "success")]
    [InlineData("failure", "success", "failure")]
    [InlineData("failure", "skipped", "neutral")]
    [InlineData("failure", null, "neutral")]
    [InlineData("cancelled", "success", "neutral")]
    public void OutcomeRequiresSuccessfulMarker(string test, string? marker, string conclusion)
    {
        var steps = new List<JobStep> { new("main-watcher-test", test) };
        if (marker is not null) steps.Add(new("main-watcher-tests-finished", marker));
        Assert.Equal(conclusion, Outcomes.Read([new("caller / main-watcher", "completed", steps)])!.Conclusion);
    }

    [Fact]
    public void ReadsRealReusableCallerJobNames()
    {
        using var json = JsonDocument.Parse(Fixture("caller-jobs.json"));
        var jobs = json.RootElement.GetProperty("jobs").EnumerateArray().Select(j => new WorkflowJob(
            j.GetProperty("name").GetString()!, j.GetProperty("status").GetString()!,
            j.GetProperty("steps").EnumerateArray().Select(s => new JobStep(s.GetProperty("name").GetString()!, s.GetProperty("conclusion").GetString())).ToArray())).ToArray();
        Assert.Equal("failure", Outcomes.Read(jobs)!.Conclusion);
        Assert.Null(Outcomes.Read([new("caller / main-watcher", "in_progress", [])]));
        Assert.Equal("neutral", Outcomes.Read(null)!.Conclusion);
        Assert.Equal("neutral", Outcomes.Read([new("main-watcher", "completed", [new("main-watcher-test", "success"), new("main-watcher-test", "failure")])])!.Conclusion);
    }

    [Fact]
    public async Task PlannerCreatesDispatchesAndLinksReturnedId()
    {
        var fake = new FakeGitHub();
        var check = await new Planner(fake, () => Now).Plan(new() { Repo = "owner/repo" }, false, TestContext.Current.CancellationToken);
        Assert.Equal("42", check!.ExternalId);
        Assert.Equal(new[] { "create", "dispatch", "link:42" }, fake.Writes);
    }

    [Fact]
    public async Task MissingDispatchIdMatchesShaInputNotBranchHead()
    {
        var fake = new FakeGitHub { DispatchId = null, RunList = [new(43, "main-watcher-tests head", Now, "queued"), new(44, "main-watcher-tests other", Now, "queued")] };
        var check = await new Planner(fake, () => Now, (_, _) => Task.CompletedTask).Plan(new() { Repo = "owner/repo" }, false, TestContext.Current.CancellationToken);
        Assert.Equal("43", check!.ExternalId);
        fake.RunList.Add(new(45, "main-watcher-tests head", Now, "queued"));
        Assert.Null(await new Planner(fake).FindRun("owner/repo", check, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReporterKeepsFailureRedWithoutArtifacts()
    {
        var fake = new FakeGitHub { ReportResult = CtrfResult.Unknown };
        Assert.True(await new Reporter(fake).Report(new() { Repo = "owner/repo" }, Check("in_progress", null), TestContext.Current.CancellationToken));
        Assert.Equal("failure", fake.Conclusion);
        Assert.Contains("failing tests unknown", fake.Summary);
    }

    [Fact]
    public async Task ReporterLeavesJobsApiErrorsPending()
    {
        var fake = new FakeGitHub { JobsError = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake).Report(new() { Repo = "owner/repo" }, Check("in_progress", null), TestContext.Current.CancellationToken));
        Assert.Null(fake.Conclusion);
    }

    [Fact]
    public void ConfigurationDefaultsAndValidation()
    {
        var target = TargetConfiguration.Parse("targets:\n  - repo: owner/repo").Targets.Single();
        Assert.Equal(15, target.PollInterval);
        Assert.Equal(30, target.Timeout);
        Assert.Throws<InvalidDataException>(() => TargetConfiguration.Parse("targets:\n  - repo: owner/repo\n    timeout: 341"));
        Assert.Throws<InvalidDataException>(() => TargetConfiguration.Parse("targets:\n  - repo: owner/repo\n  - repo: OWNER/REPO"));
        Assert.ThrowsAny<Exception>(() => TargetConfiguration.Parse("targets:\n  - repo: owner/repo\n    typo: true"));
        Assert.ThrowsAny<Exception>(() => TargetConfiguration.Parse("targets:\n  - repo: owner/repo\n    enabled: true\n    enabled: false"));
    }

    [Fact]
    public async Task RejectedDispatchDoesNotLeaveCheckInProgress()
    {
        var fake = new FakeGitHub { DispatchError = new HttpRequestException("Rejected", null, System.Net.HttpStatusCode.UnprocessableEntity) };
        await Assert.ThrowsAsync<HttpRequestException>(() => new Planner(fake, () => Now)
            .Plan(new() { Repo = "owner/repo" }, false, TestContext.Current.CancellationToken));
        Assert.Equal("neutral", fake.Conclusion);
    }

    [Fact]
    public async Task InvisibleDispatchReturnsPendingInsteadOfAbortingTheCycle()
    {
        var fake = new FakeGitHub { DispatchId = null };
        var check = await new Planner(fake, () => Now, (_, _) => Task.CompletedTask)
            .Plan(new() { Repo = "owner/repo" }, false, TestContext.Current.CancellationToken);
        Assert.Equal("in_progress", check!.Status);
        Assert.Null(check.ExternalId);
        Assert.Single(fake.Writes, w => w == "dispatch");
    }

    [Theory]
    [InlineData(29, "in_progress")]
    [InlineData(30, "completed")]
    public async Task MissingDispatchIsReleasedOnlyAfterVisibilityWindow(int age, string expected)
    {
        var fake = new FakeGitHub();
        var pending = Check("in_progress", null) with { ExternalId = null, StartedAt = Now.AddMinutes(-age) };
        var result = await new Planner(fake, () => Now).Recover("owner/repo", pending, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.Equal(age == 30 ? "neutral" : null, fake.Conclusion);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public async Task RecoveryLinksLateRunAndNeverReleasesAmbiguousRuns()
    {
        var pending = Check("in_progress", null) with { ExternalId = null };
        var fake = new FakeGitHub { RunList = [new(42, "main-watcher-tests head", pending.StartedAt, "queued")] };
        var planner = new Planner(fake, () => Now);
        Assert.Equal("42", (await planner.Recover("owner/repo", pending, TestContext.Current.CancellationToken)).ExternalId);
        fake.RunList.Add(new(43, "main-watcher-tests head", pending.StartedAt, "queued"));
        Assert.Equal("in_progress", (await planner.Recover("owner/repo", pending, TestContext.Current.CancellationToken)).Status);
        Assert.Null(fake.Conclusion);
    }

    [Theory]
    [InlineData("test-command: other")]
    [InlineData("results-glob: other/*.json")]
    [InlineData("timeout-minutes: 31")]
    [InlineData("timeout-minutes: ${{ inputs.timeout }}")]
    public void CallerMismatchIsRejected(string setting)
    {
        Assert.Throws<InvalidDataException>(() => CallerConfiguration.Validate(new(),
            "jobs:\n  tests:\n    uses: owner/repo/.github/workflows/run-integration-tests.yml@v1\n    with:\n      " + setting));
    }

    [Fact]
    public void CallerDefaultsMatchConfigurationDefaults() => CallerConfiguration.Validate(new(),
        "jobs:\n  tests:\n    uses: owner/repo/.github/workflows/run-integration-tests.yml@v1");

    [Fact]
    public async Task RecoveryReadFailureNeverReleasesOrRedispatches()
    {
        var fake = new FakeGitHub { RunsError = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => new Planner(fake, () => Now)
            .Recover("owner/repo", Check("in_progress", null) with { ExternalId = null }, TestContext.Current.CancellationToken));
        Assert.Null(fake.Conclusion);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public async Task CallerMismatchCreatesNoCheckAndDispatchesNothing()
    {
        var fake = new FakeGitHub { InvalidCaller = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => new Planner(fake, () => Now)
            .Plan(new() { Repo = "owner/repo" }, false, TestContext.Current.CancellationToken));
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void ConfigurationDefaultsMatchTheActualReusableWorkflow()
    {
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        yaml.Load(new StringReader(Fixture("run-integration-tests.yml")));
        var root = (YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode;
        var inputs = root.Children["on"]["workflow_call"]["inputs"];
        var target = new Target();
        Assert.Equal(target.TestCommand, inputs["test-command"]["default"].ToString());
        Assert.Equal(target.ResultsGlob, inputs["results-glob"]["default"].ToString());
        Assert.Equal(target.Timeout.ToString(System.Globalization.CultureInfo.InvariantCulture), inputs["timeout-minutes"]["default"].ToString());
    }

    static CheckRun Pending(string sha = "abcdef1234567890") => Check("in_progress", null, sha);

    [Fact]
    public async Task RedResultOpensAppLockBeforeCompletingTheCheck()
    {
        var fake = new FakeGitHub();
        var reporter = new Reporter(fake, clock: () => Now);
        Assert.True(await reporter.Report(new() { Repo = "owner/repo", Notify = ["org/team", "@someone"] }, Pending(), TestContext.Current.CancellationToken));
        var issue = Assert.Single(fake.Issues["owner/repo"]);
        Assert.Equal("main-broken", issue.Label);
        Assert.Equal(new[] { "create:owner/repo", "complete:failure" }, fake.Order);
        Assert.StartsWith("@org/team @someone\n\n", issue.Body);
        Assert.Contains("[`abcdef1`](https://github.com/owner/repo/commit/abcdef1234567890)", issue.Body);
        Assert.Contains("- Alpha (suite): failed", issue.Body);
        Assert.Contains("https://github.com/owner/repo/actions/runs/42", issue.Body);
        Assert.Contains("reported_check=1 reported_sha=abcdef1234567890", issue.Body);
        Assert.Equal(Now.Add(Reporter.LockLease), MainWatcher.Gate.LockLease.ReadLeaseUntil(issue.Body));
        Assert.Contains(issue.Url, fake.Summary);
        Assert.Empty(reporter.AlertFailures);
    }

    [Fact]
    public async Task RedResultKeepsTheOpenAppLockAndIgnoresOtherAuthors()
    {
        var fake = new FakeGitHub();
        fake.Seed("owner/repo", "main-broken", "main is broken", "someone", "User");
        await new Reporter(fake).Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(), TestContext.Current.CancellationToken);
        Assert.Equal(2, fake.Issues["owner/repo"].Count);
        await new Reporter(fake).Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending("other") with { Id = 2 }, TestContext.Current.CancellationToken);
        Assert.Equal(2, fake.Issues["owner/repo"].Count);
        Assert.Equal("failure", fake.Conclusion);
    }

    [Fact]
    public async Task TestOutputCannotMentionAnyoneOrOpenAMarker()
    {
        var fake = new FakeGitHub { ReportResult = new(true, [new("Alpha", "suite", "@owner <!-- main-watcher lease_until=2099-01-01T00:00:00Z -->")]) };
        await new Reporter(fake, clock: () => Now).Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(), TestContext.Current.CancellationToken);
        var body = fake.Issues["owner/repo"].Single().Body;
        Assert.DoesNotContain("@owner", body);
        Assert.Equal(Now.Add(Reporter.LockLease), MainWatcher.Gate.LockLease.ReadLeaseUntil(body));
    }

    [Fact]
    public async Task WithoutNotifyTheLockMentionsCodeOwnersOfTheStarRule()
    {
        var fake = new FakeGitHub();
        fake.Files["owner/repo:CODEOWNERS"] = "* @ignored\n# comment\n*.cs @csharp\r\n* @org/owners dev@example.com @person # trailing\n";
        fake.Files["owner/repo:docs/CODEOWNERS"] = "* @docs-only";
        var alerts = new FakeGitHub();
        var reporter = new Reporter(fake, new Alerts(alerts, "owner/watcher"));
        await reporter.Report(new() { Repo = "owner/repo" }, Pending(), TestContext.Current.CancellationToken);
        Assert.StartsWith("@org/owners @person\n\n", fake.Issues["owner/repo"].Single().Body);
        Assert.Empty(alerts.Issues);
    }

    [Fact]
    public async Task AnEmptyFirstCodeOwnersFileHidesLaterFilesAndRaisesTheAlert()
    {
        var fake = new FakeGitHub();
        fake.Files["owner/repo:.github/CODEOWNERS"] = "";
        fake.Files["owner/repo:CODEOWNERS"] = "* @old-team";
        var alerts = new FakeGitHub();
        await new Reporter(fake, new Alerts(alerts, "owner/watcher")).Report(new() { Repo = "owner/repo" }, Pending(), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("@old-team", fake.Issues["owner/repo"].Single().Body);
        Assert.Single(alerts.Issues["owner/watcher"]);
    }

    [Fact]
    public async Task OversizedFailuresKeepTheLockBodyWithinGitHubsLimit()
    {
        var huge = new string('@', 100_000);
        var failures = Enumerable.Range(0, 5000).Select(i => new FailedTest(huge + i, huge, "message")).ToArray();
        var fake = new FakeGitHub
        {
            ReportResult = new(true, failures),
            Activity = [.. Enumerable.Range(0, 100).Select(i => new Push(Sha('a'), Sha('b'), Now, huge, huge))],
        };
        await new Reporter(fake, clock: () => Now).Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(), TestContext.Current.CancellationToken);
        var body = fake.Issues["owner/repo"].Single().Body;
        Assert.True(body.Length <= Reporter.MaxIssueBody, $"body is {body.Length} characters");
        Assert.Contains("more; see the target run", body);
        Assert.Contains("https://github.com/owner/repo/actions/runs/42", body);
        Assert.Equal(Now.Add(Reporter.LockLease), MainWatcher.Gate.LockLease.ReadLeaseUntil(body));
        Assert.Contains("reported_check=1", body);
    }

    [Fact]
    public async Task WithNobodyToMentionTheLockOpensAndOneDeduplicatedAlertIsRaised()
    {
        var fake = new FakeGitHub();
        var alerts = new FakeGitHub();
        var reporter = new Reporter(fake, new Alerts(alerts, "owner/watcher"));
        await reporter.Report(new() { Repo = "owner/repo" }, Pending(), TestContext.Current.CancellationToken);
        Assert.StartsWith("Main Watcher tests failed", fake.Issues["owner/repo"].Single().Body);
        var alert = Assert.Single(alerts.Issues["owner/watcher"]);
        Assert.Equal("watcher-infra", alert.Label);
        Assert.Contains("notify", alert.Body);

        fake.Issues.Clear();
        await reporter.Report(new() { Repo = "owner/repo" }, Pending("next") with { Id = 2 }, TestContext.Current.CancellationToken);
        Assert.Single(alerts.Issues["owner/watcher"]);
        Assert.Equal(new[] { "create:owner/watcher", "comment:1" }, alerts.Order);
        Assert.Empty(reporter.AlertFailures);
    }

    [Fact]
    public async Task AlertFailureNeverBlocksTheLockOrTheCheck()
    {
        var fake = new FakeGitHub();
        var reporter = new Reporter(fake, new Alerts(new FakeGitHub { IssueError = true }, "owner/watcher"));
        await reporter.Report(new() { Repo = "owner/repo" }, Pending(), TestContext.Current.CancellationToken);
        Assert.Single(fake.Issues["owner/repo"]);
        Assert.Equal("failure", fake.Conclusion);
        Assert.Single(reporter.AlertFailures);
    }

    [Fact]
    public async Task FailedIssueWriteLeavesTheCheckInProgress()
    {
        var fake = new FakeGitHub { IssueError = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake)
            .Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(), TestContext.Current.CancellationToken));
        Assert.Null(fake.Conclusion);
    }

    [Fact]
    public async Task GreenResultClosesEveryOpenAppLockBeforeCompletingTheCheck()
    {
        var fake = new FakeGitHub { JobConclusion = "success", ReportResult = new(true, []) };
        fake.Seed("owner/repo", "main-broken", "main is broken", "main-watcher[bot]", "Bot");
        fake.Seed("owner/repo", "main-broken", "hand-made", "someone", "User");
        await new Reporter(fake).Report(new() { Repo = "owner/repo" }, Pending(), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "comment:1", "close:1", "complete:success" }, fake.Order);
        Assert.Contains("abcdef1", fake.Comments.Single());
        Assert.Equal("hand-made", Assert.Single(fake.Issues["owner/repo"], i => i.Open).Issue.Title);
    }

    [Fact]
    public async Task FailedCloseLeavesTheCheckInProgress()
    {
        var fake = new FakeGitHub { JobConclusion = "success", ReportResult = new(true, []), IssueError = true };
        fake.Seed("owner/repo", "main-broken", "main is broken", "main-watcher[bot]", "Bot");
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake)
            .Report(new() { Repo = "owner/repo" }, Pending(), TestContext.Current.CancellationToken));
        Assert.Null(fake.Conclusion);
    }

    static string Sha(char c) => new(c, 40);
    static CheckRun Result(long id, char sha, string conclusion, int minutesAgo) =>
        new(id, Sha(sha), "completed", conclusion, Now.AddMinutes(-minutesAgo), Now.AddMinutes(-minutesAgo + 5), id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    static Push PushAt(char before, char after, string type, string? actor, int minutesAgo) =>
        new(Sha(before), Sha(after), Now.AddMinutes(-minutesAgo), type, actor);

    [Fact]
    public async Task TheLockListsEveryPushSinceTheNewestGreenCommit()
    {
        var fake = new FakeGitHub
        {
            // The failing head, a commit whose newest result is neutral after an older success (ADR-017), the green commit.
            HistoryShas = [Sha('f'), Sha('b'), Sha('a'), Sha('9')],
            CommitCheckRuns = new()
            {
                [Sha('b')] = [Result(20, 'b', "success", 50), Result(21, 'b', "neutral", 30)],
                [Sha('a')] = [Result(10, 'a', "success", 90)],
                [Sha('9')] = [Result(5, '9', "success", 200)],
            },
            Activity =
            [
                PushAt('c', 'f', "merge_queue_merge", "github-merge-queue[bot]", 10),
                PushAt('d', 'c', "force_push", "alice", 20),
                PushAt('b', 'd', "pr_merge", "bob", 25),
                PushAt('a', 'b', "push", "carol", 60),
                PushAt('9', 'a', "push", "dave", 100),
            ],
            Counts = new() { [Sha('f')] = 3, [Sha('c')] = 1, [Sha('b')] = 2 },
        };
        var reporter = new Reporter(fake, clock: () => Now);
        await reporter.Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(Sha('f')) with { Id = 30 }, TestContext.Current.CancellationToken);
        // The failing head is skipped; b (neutral over success) and a were checked.
        Assert.Equal(new WalkBack(30, 2, PushSource.SinceGreen), Assert.Single(reporter.WalkBacks));
        var body = fake.Issues["owner/repo"].Single().Body;
        Assert.Contains($"Since the last green commit, [`aaaaaaa`](https://github.com/owner/repo/commit/{Sha('a')}).", body);
        Assert.Contains($"| 2026-09-16 18:50:00 | github-merge-queue\\[bot] | merge-queue merge | [`ccccccc` → `fffffff`](https://github.com/owner/repo/compare/{Sha('c')}...{Sha('f')}) | 3 |", body);
        Assert.Contains("| 2026-09-16 18:40:00 | alice | force push |", body);
        Assert.Contains("| bob | PR merge |", body);
        Assert.Contains("| carol | push | [`aaaaaaa` → `bbbbbbb`]", body);
        Assert.Contains("`ddddddd` → `ccccccc`](https://github.com/owner/repo/compare/", body);
        Assert.Contains("| ? |", body);
        Assert.DoesNotContain("dave", body);
        Assert.DoesNotContain("@alice", body);
        Assert.Contains($"<!-- main-watcher last_green={Sha('a')} first_red={Sha('f')} ", body);
        Assert.Equal(Now.Add(Reporter.LockLease), MainWatcher.Gate.LockLease.ReadLeaseUntil(body));
    }

    [Theory]
    // The push that made the green commit the head came before its run started, or seconds after it by clock skew.
    [InlineData(70)]
    [InlineData(59)]
    public async Task ARollbackToTheGreenCommitStillListsThePushThatBrokeMain(int greenPushMinutesAgo)
    {
        var fake = new FakeGitHub
        {
            HistoryShas = [Sha('f'), Sha('a')],
            CommitCheckRuns = new() { [Sha('a')] = [Result(10, 'a', "success", 60)] },
            // Green a, then a→f was tested and failed, and f was reset back to a before the report.
            Activity = [PushAt('f', 'a', "force_push", "alice", 5), PushAt('a', 'f', "push", "bob", 30), PushAt('9', 'a', "push", "carol", greenPushMinutesAgo)],
        };
        var reporter = new Reporter(fake);
        await reporter.Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(Sha('f')) with { Id = 30 }, TestContext.Current.CancellationToken);
        var body = fake.Issues["owner/repo"].Single().Body;
        Assert.Contains($"Since the last green commit, [`aaaaaaa`]", body);
        Assert.Contains("| alice | force push |", body);
        Assert.Contains("| bob | push |", body);
        Assert.DoesNotContain("carol", body);
        Assert.Equal(new WalkBack(30, 1, PushSource.SinceGreen), Assert.Single(reporter.WalkBacks));
    }

    [Fact]
    public async Task AGreenPushOutsideTheActivityReadKeepsEveryPushReadInsteadOfStoppingAtALaterRollback()
    {
        var fake = new FakeGitHub
        {
            HistoryShas = [Sha('f'), Sha('a')],
            CommitCheckRuns = new() { [Sha('a')] = [Result(10, 'a', "success", 60)] },
            // The push that made a the head for its green run is older than the 100 entries read; the newest is a rollback to a.
            Activity = [PushAt('f', 'a', "force_push", "alice", 3), .. Enumerable.Range(0, 99).Select(i => PushAt('a', 'f', "push", $"user{i}", 5 + i % 50))],
        };
        await new Reporter(fake).Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(Sha('f')), TestContext.Current.CancellationToken);
        var body = fake.Issues["owner/repo"].Single().Body;
        Assert.DoesNotContain("No pushes found", body);
        Assert.Contains("| alice | force push |", body);
        Assert.Contains("| user98 | push |", body);
        Assert.Contains("The repository activity read does not reach back far enough", body);
    }

    [Theory]
    // A rollback more than ClockSkew after the green run started is not mistaken for the green run's push.
    [InlineData(1, false)]
    [InlineData(3, true)]
    public async Task OnlyABoundedClockSkewLetsALaterPushToGreenBeTheBoundary(int minutesAfterStart, bool listed)
    {
        var fake = new FakeGitHub
        {
            HistoryShas = [Sha('f'), Sha('a')],
            CommitCheckRuns = new() { [Sha('a')] = [Result(10, 'a', "success", 60)] },
            Activity = [PushAt('f', 'a', "force_push", "alice", 60 - minutesAfterStart), PushAt('a', 'f', "push", "bob", 70)],
        };
        await new Reporter(fake).Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(Sha('f')), TestContext.Current.CancellationToken);
        var body = fake.Issues["owner/repo"].Single().Body;
        Assert.Equal(listed, body.Contains("| alice | force push |"));
        Assert.Equal(listed, body.Contains("The repository activity read does not reach back far enough"));
    }

    [Fact]
    public async Task AForcePushedGreenCommitListsActivityAfterItsCheckRun()
    {
        var fake = new FakeGitHub
        {
            HistoryShas = [Sha('f'), Sha('e')],
            CommitCheckRuns = new() { [Sha('a')] = [Result(10, 'a', "success", 60)] },
            Activity = [PushAt('a', 'f', "force_push", "alice", 30), PushAt('0', 'a', "push", "bob", 70)],
        };
        var reporter = new Reporter(fake);
        await reporter.Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(Sha('f')), TestContext.Current.CancellationToken);
        var body = fake.Issues["owner/repo"].Single().Body;
        Assert.Contains("The last green commit, `aaaaaaa`, is not among the newest 100 commits of `main`", body);
        Assert.Contains("after its check run started at 2026-09-16 18:00:00 UTC", body);
        Assert.Contains("| alice | force push |", body);
        Assert.DoesNotContain("bob", body);
        Assert.Contains($"last_green={Sha('a')} ", body);
        // History commit e, then the activity's a; f was already walked.
        Assert.Equal(new WalkBack(1, 2, PushSource.AfterGreenCheck), Assert.Single(reporter.WalkBacks));
    }

    [Fact]
    public async Task WithoutAnyGreenRunTheLockListsTheLastHundredPushes()
    {
        var fake = new FakeGitHub
        {
            HistoryShas = [Sha('f')],
            Activity = [.. Enumerable.Range(0, 150).Select(i => PushAt('a', 'b', "push", $"user{i}", i + 1)), PushAt('0', 'a', "branch_creation", null, 500)],
        };
        await new Reporter(fake).Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(Sha('f')), TestContext.Current.CancellationToken);
        var body = fake.Issues["owner/repo"].Single().Body;
        Assert.Contains("No green Main Watcher run was found, so this lists the last 100 pushes.", body);
        Assert.Contains("| user99 |", body);
        Assert.DoesNotContain("| user100 |", body);
        Assert.Contains("The repository activity read does not reach back far enough", body);
        Assert.DoesNotContain("last_green=", body);
    }

    [Fact]
    public async Task AnUnreadablePushListStillOpensTheLockWithACompareLink()
    {
        var fake = new FakeGitHub
        {
            HistoryShas = [Sha('f'), Sha('a')],
            CommitCheckRuns = new() { [Sha('a')] = [Result(10, 'a', "success", 60)] },
            ActivityError = true,
        };
        await new Reporter(fake).Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(Sha('f')), TestContext.Current.CancellationToken);
        var body = fake.Issues["owner/repo"].Single().Body;
        Assert.Contains($"Push list unavailable: the repository activity could not be read. [Compare `aaaaaaa...fffffff`](https://github.com/owner/repo/compare/{Sha('a')}...{Sha('f')})", body);
        Assert.Contains($"last_green={Sha('a')} ", body);
        Assert.Equal("failure", fake.Conclusion);
    }

    [Fact]
    public async Task ALaterFailingRunAddsOneCommentThatMentionsNobody()
    {
        var fake = new FakeGitHub { ReportResult = new(true, [new("Beta", "suite", "@owner broke it")]) };
        fake.Seed("owner/repo", "main-broken", "main is broken", "main-watcher[bot]", "Bot", "@team\n\nfirst failure");
        await new Reporter(fake).Report(new() { Repo = "owner/repo", Notify = ["team"] }, Pending(Sha('e')) with { Id = 77 }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "comment:1", "update:1", "complete:failure" }, fake.Order);
        var comment = Assert.Single(fake.Comments);
        Assert.DoesNotContain("@", comment);
        Assert.Contains("Tests failed again on `main` at [`eeeeeee`]", comment);
        Assert.Contains("- Beta (suite):", comment);
        Assert.Contains("https://github.com/owner/repo/actions/runs/42", comment);
        Assert.EndsWith($"<!-- main-watcher check=77 sha={Sha('e')} -->", comment);
        Assert.Equal("first failure", fake.Issues["owner/repo"].Single().Body.Split("\n\n")[1]);
        Assert.EndsWith($"<!-- main-watcher reported_check=77 reported_sha={Sha('e')} -->", fake.Issues["owner/repo"].Single().Body);
    }

    [Fact]
    public async Task AlertsCommentOnTheOpenAlertWithTheSameTitle()
    {
        var fake = new FakeGitHub();
        fake.Seed("owner/watcher", "watcher-infra", "Other alert", "github-actions[bot]", "Bot");
        var alerts = new Alerts(fake, "owner/watcher");
        await alerts.Raise("Target down", "first", TestContext.Current.CancellationToken);
        await alerts.Raise("Target down", "again", TestContext.Current.CancellationToken);
        Assert.Equal(2, fake.Issues["owner/watcher"].Count);
        Assert.Equal(new[] { "create:owner/watcher", "comment:2" }, fake.Order);
        Assert.Equal("again", fake.Comments.Single());
    }

    [Theory]
    [InlineData("user", true)]
    [InlineData("@org/team-name", true)]
    [InlineData("@", false)]
    [InlineData("two words", false)]
    [InlineData("user)[link](x", false)]
    public void NotifyAcceptsOnlyHandles(string handle, bool valid)
    {
        var yaml = $"targets:\n  - repo: owner/repo\n    notify: ['{handle}']";
        if (valid) Assert.Single(TargetConfiguration.Parse(yaml).Targets);
        else Assert.Throws<InvalidDataException>(() => TargetConfiguration.Parse(yaml));
    }

    [Fact]
    public void StarRuleWithoutOwnersClearsEarlierOwners() =>
        Assert.Empty(Mentions.StarOwners("* @first\n*\n"));

    static readonly Target Watched = new() { Repo = "owner/repo", Notify = ["team"] };
    static string LockBody(long check, char sha) =>
        $"@team\n\nfirst failure\n\n<!-- main-watcher first_red={Sha(sha)} lease_until=2026-09-16T23:00:00Z reported_check={check} reported_sha={Sha(sha)} -->";
    static FakeIssue SeedLock(FakeGitHub fake, long check, char sha) => fake.Seed("owner/repo", "main-broken", "main is broken", "main-watcher[bot]", "Bot", LockBody(check, sha));

    // TS-U8 (ADR-013): create, update and close sequences, each stopped after every issue write.
    [Theory]
    [InlineData("create", 1)]
    [InlineData("update", 1)]
    [InlineData("update", 2)]
    [InlineData("close", 1)]
    [InlineData("close", 2)]
    public async Task AReportStoppedAfterAnyIssueWriteIsReplayedWithoutRepeatingAWrite(string sequence, int stopAfter)
    {
        var ct = TestContext.Current.CancellationToken;
        var green = sequence == "close";
        var fake = new FakeGitHub { JobConclusion = green ? "success" : "failure", ReportResult = new(true, green ? [] : [new("Alpha", "suite", "failed")]) };
        if (sequence != "create") SeedLock(fake, 1, 'a');
        var check = Pending(Sha('b')) with { Id = 2 };
        fake.StopAfterWrites = stopAfter;
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake, clock: () => Now).Report(Watched, check, ct));
        Assert.Null(fake.Conclusion);

        fake.StopAfterWrites = null;
        Assert.True(await new Reporter(fake, clock: () => Now).Report(Watched, check, ct));
        var issue = Assert.Single(fake.Issues["owner/repo"]);
        Assert.Equal(sequence == "create" ? 0 : 1, fake.Comments.Count);
        Assert.Equal(green ? "success" : "failure", fake.Conclusion);
        Assert.Equal(green, !issue.Open);
        if (!green) Assert.Contains($"reported_check=2 reported_sha={Sha('b')} -->", issue.Body);
        Assert.Equal(fake.Order.Distinct(), fake.Order);
        Assert.Equal(1, fake.Order.Count(o => o.StartsWith("complete:")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OfTwoOpenLocksTheNewerIsClosedAsADuplicateAndCarriesNoResult(int? stopAfter)
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub();
        SeedLock(fake, 1, 'a');
        // A replay whose issue list lagged opened a second lock for check 2.
        SeedLock(fake, 2, 'b');
        var check = Pending(Sha('b')) with { Id = 2 };
        var writes = new List<string>();
        fake.StopAfterWrites = stopAfter;
        if (stopAfter is not null)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake, afterWrite: writes.Add).Report(Watched, check, ct));
            fake.StopAfterWrites = null;
        }
        await new Reporter(fake, afterWrite: writes.Add).Report(Watched, check, ct);

        // The canonical lock is named by its database ID.
        Assert.Equal(new[] { "comment:2", "close:2:duplicate:1001", "comment:1", "update:1", "complete:failure" }, fake.Order);
        // The hook the sandbox fault switch uses names each write.
        if (stopAfter is null) Assert.Equal(new[] { "comment", "close", "comment", "update" }, writes);
        Assert.Contains("Closing as a duplicate of #1", Assert.Single(fake.Find("owner/repo", 2).Comments).Body);
        Assert.Equal("duplicate", fake.Find("owner/repo", 2).Issue.StateReason);
        var kept = fake.Find("owner/repo", 1);
        Assert.True(kept.Open);
        Assert.Contains("Tests failed again", Assert.Single(kept.Comments).Body);
        Assert.Contains("reported_check=2", kept.Body);
        Assert.Contains("issues/1", fake.Summary);
    }

    [Fact]
    public async Task AReplayAfterAHumanClosedTheNewLockCreatesNothingAndTheOverrideIsNoted()
    {
        // TS-S14 (d): stopped after creating the lock, which a human closes before the replay.
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { StopAfterWrites = 1 };
        var check = Pending(Sha('b')) with { Id = 2 };
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake, clock: () => Now).Report(Watched, check, ct));
        fake.StopAfterWrites = null;
        fake.CloseByHand("owner/repo", 1, "alice");

        var reporter = new Reporter(fake, clock: () => Now);
        await reporter.Report(Watched, check, ct);
        Assert.Equal(new[] { "create:owner/repo", "complete:failure" }, fake.Order);
        Assert.Contains("already written to lock issue https://github.com/owner/repo/issues/1, which has been closed since", fake.Summary);

        Assert.Equal(1, await reporter.NoteOverrides(Watched, ct));
        Assert.StartsWith("`alice` closed this lock by hand. That is an override", Assert.Single(fake.Comments));
        Assert.Equal(0, await reporter.NoteOverrides(Watched, ct));
    }

    [Fact]
    public async Task AReplayOfAnInterruptedCreateStillRaisesTheNobodyMentionedAlert()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { StopAfterWrites = 1 };
        var alerts = new FakeGitHub();
        var target = new Target { Repo = "owner/repo" };
        var check = Pending(Sha('b')) with { Id = 2 };
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake, new Alerts(alerts, "owner/watcher")).Report(target, check, ct));
        Assert.Empty(alerts.Issues);
        fake.StopAfterWrites = null;
        await new Reporter(fake, new Alerts(alerts, "owner/watcher")).Report(target, check, ct);
        Assert.Contains("issues/1 mentions nobody", Assert.Single(alerts.Issues["owner/watcher"]).Body);
        Assert.Equal(new[] { "create:owner/repo", "complete:failure" }, fake.Order);
    }

    [Fact]
    public async Task AReplayFindsItsCommentOnALockClosedSinceAndCreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub();
        SeedLock(fake, 1, 'a');
        var check = Pending(Sha('b')) with { Id = 2 };
        fake.StopAfterWrites = 1;
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake).Report(Watched, check, ct));
        fake.StopAfterWrites = null;
        fake.CloseByHand("owner/repo", 1);
        await new Reporter(fake).Report(Watched, check, ct);
        Assert.Equal(new[] { "comment:1", "complete:failure" }, fake.Order);
    }

    [Theory]
    // A red result for the commit of a lock closed by hand is covered by the override.
    [InlineData('a', "User", false)]
    // A different commit locks again, and the new lock links to the overridden one.
    [InlineData('b', "User", true)]
    // A lock the App closed is no override.
    [InlineData('a', "Bot", true)]
    public async Task AnOverrideCoversOnlyItsOwnCommit(char sha, string closerType, bool locks)
    {
        var fake = new FakeGitHub();
        var closed = SeedLock(fake, 1, 'a');
        closed.Issue = closed.Issue with { State = "closed", StateReason = "completed" };
        closed.ClosedBy = closerType == "Bot" ? new("main-watcher[bot]", "Bot") : new("alice", "User");
        await new Reporter(fake).Report(Watched, Pending(Sha(sha)) with { Id = 5 }, TestContext.Current.CancellationToken);
        Assert.Equal("failure", fake.Conclusion);
        Assert.Equal(locks ? new[] { "create:owner/repo", "complete:failure" } : ["complete:failure"], fake.Order);
        if (!locks) Assert.Contains("was closed by hand (an override), so no lock was opened", fake.Summary);
        else Assert.Equal(closerType == "User", fake.Find("owner/repo", 2).Body.Contains("The previous lock, https://github.com/owner/repo/issues/1, was closed by hand."));
    }

    [Fact]
    public async Task AnOverrideCoversACommitThatOnlyAnInterruptedCommentReported()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub();
        SeedLock(fake, 1, 'a');
        // Check 2 on b commented, then stopped before the body marker; a human closed the lock.
        fake.StopAfterWrites = 1;
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake).Report(Watched, Pending(Sha('b')) with { Id = 2 }, ct));
        fake.StopAfterWrites = null;
        fake.CloseByHand("owner/repo", 1);
        Assert.Contains($"reported_sha={Sha('a')}", fake.Find("owner/repo", 1).Body);
        // Check 2's run was deleted, so it ended neutral without a replay; a retest of b fails.
        await new Reporter(fake).Report(Watched, Pending(Sha('b')) with { Id = 3 }, ct);
        Assert.Equal(new[] { "comment:1", "complete:failure" }, fake.Order);
        Assert.Contains("was closed by hand (an override), so no lock was opened", fake.Summary);
    }

    [Theory]
    [InlineData("green", "`alice` closed this lock by hand after tests had passed on `main` at [`ccccccc`]")]
    [InlineData("duplicate", "`alice` closed this lock by hand. It had already been marked a duplicate of #1, which decides whether `main` stays locked.")]
    public async Task AHumanCloseAfterTheAppStartedClosingStillGetsACommentNamingWhoClosedIt(string started, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { JobConclusion = started == "green" ? "success" : "failure", ReportResult = new(true, []), StopAfterWrites = 1 };
        if (started == "duplicate") SeedLock(fake, 1, 'a');
        var stopped = SeedLock(fake, 2, 'b');
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake).Report(Watched, Pending(Sha('c')) with { Id = 3 }, ct));
        fake.StopAfterWrites = null;
        fake.CloseByHand("owner/repo", stopped.Issue.Number, "alice");
        var reporter = new Reporter(fake, clock: () => Now);
        Assert.Equal(1, await reporter.NoteOverrides(Watched, ct));
        Assert.StartsWith(expected, stopped.Comments[^1].Body);
        Assert.EndsWith("<!-- main-watcher closed=override -->", stopped.Comments[^1].Body);
        Assert.Equal(0, await reporter.NoteOverrides(Watched, ct));
    }

    [Fact]
    public async Task OnlyLocksClosedByHandGetAnOverrideCommentNamingWhoClosedThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub();
        SeedLock(fake, 1, 'a');
        fake.CloseByHand("owner/repo", 1, "alice");
        // Closed by a green run.
        SeedLock(fake, 2, 'b');
        await new Reporter(fake).Report(Watched, Pending(Sha('c')) with { Id = 3 }, ct);
        fake.Order.Clear();
        // Closed by the App before closing comments carried a marker.
        var legacy = SeedLock(fake, 4, 'd');
        legacy.Issue = legacy.Issue with { State = "closed" };
        legacy.ClosedBy = new("main-watcher[bot]", "Bot");
        fake.Seed("owner/repo", "main-broken", "hand-made", "someone", "User");
        fake.CloseByHand("owner/repo", 4, "bob");
        SeedLock(fake, 6, 'e');
        fake.CloseByHand("owner/repo", 5, null);
        // Outside reconcile_lookback.
        var old = SeedLock(fake, 7, 'f');
        fake.CloseByHand("owner/repo", 6, "carol");
        old.Issue = old.Issue with { UpdatedAt = Now - Reporter.ReconcileLookback - TimeSpan.FromMinutes(1) };

        var reporter = new Reporter(fake, clock: () => Now);
        Assert.Equal(2, await reporter.NoteOverrides(Watched, ct));
        Assert.Equal(new[] { "comment:1", "comment:5" }, fake.Order);
        var comment = Assert.Single(fake.Find("owner/repo", 1).Comments).Body;
        Assert.Equal($"`alice` closed this lock by hand. That is an override: the merge queue accepts every pull request again, but `main` is still red at [`aaaaaaa`](https://github.com/owner/repo/commit/{Sha('a')}). "
            + "Main Watcher never reopens this issue; it opens a new lock when tests fail on a different commit.\n\n<!-- main-watcher closed=override -->", comment);
        Assert.StartsWith("Someone closed this lock by hand.", Assert.Single(fake.Find("owner/repo", 5).Comments).Body);
        Assert.Equal(0, await reporter.NoteOverrides(Watched, ct));
    }

    sealed class FakeIssue(Issue issue, string label)
    {
        public Issue Issue { get; set; } = issue;
        public string Label => label;
        public Account? ClosedBy { get; set; }
        public List<IssueComment> Comments { get; } = [];
        public string Body => Issue.Body!;
        public string Url => Issue.Url;
        public bool Open => Issue.State == "open";
    }

    sealed class FakeGitHub : IGitHubGateway
    {
        public Dictionary<string, List<FakeIssue>> Issues { get; } = [];
        public Dictionary<string, string> Files { get; } = [];
        public List<string> Order { get; } = [];
        public List<string> Comments { get; } = [];
        public bool IssueError { get; init; }
        public string JobConclusion { get; init; } = "failure";
        /// <summary>Stops the report, as a crash would, right after this many issue writes succeed.</summary>
        public int? StopAfterWrites { get; set; }
        public int ClosedByReads { get; private set; }
        int next;
        int writes;

        void Wrote(string entry)
        {
            Order.Add(entry);
            if (++writes == StopAfterWrites) throw new HttpRequestException("stopped after a write");
        }

        public FakeIssue Seed(string repo, string label, string title, string author, string type, string body = "")
        {
            if (!Issues.TryGetValue(repo, out var list)) Issues[repo] = list = [];
            next++;
            list.Add(new(new(next, title, body, author, type, $"https://github.com/{repo}/issues/{next}", Id: 1000 + next), label));
            return list[^1];
        }

        public FakeIssue Find(string repo, int number) => Issues[repo].Single(i => i.Issue.Number == number);

        public void CloseByHand(string repo, int number, string? login = "alice")
        {
            var issue = Find(repo, number);
            issue.Issue = issue.Issue with { State = "closed", StateReason = "completed" };
            issue.ClosedBy = login is null ? null : new(login, "User");
        }

        public Task<string?> File(string repo, string path, CancellationToken ct) => Task.FromResult(Files.GetValueOrDefault($"{repo}:{path}"));
        public Task<IReadOnlyList<Issue>> OpenIssues(string repo, string label, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Issue>>(Issues.GetValueOrDefault(repo, []).Where(i => i.Label == label && i.Open).Select(i => i.Issue).ToArray());
        Task<IReadOnlyList<Issue>> IGitHubGateway.Issues(string repo, string label, DateTimeOffset since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Issue>>(Issues.GetValueOrDefault(repo, [])
                .Where(i => i.Label == label && (i.Issue.UpdatedAt is null || i.Issue.UpdatedAt >= since)).Select(i => i.Issue).ToArray());
        Task<IReadOnlyList<IssueComment>> IGitHubGateway.Comments(string repo, int number, DateTimeOffset? since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IssueComment>>(Find(repo, number).Comments.ToArray());
        public Task<Account?> ClosedBy(string repo, int number, CancellationToken ct)
        {
            ClosedByReads++;
            return Task.FromResult(Find(repo, number).ClosedBy);
        }
        public Task<Issue> CreateIssue(string repo, string title, string body, string label, CancellationToken ct)
        {
            if (IssueError) throw new HttpRequestException("issues unavailable");
            var issue = Seed(repo, label, title, "main-watcher[bot]", "Bot", body);
            Wrote($"create:{repo}");
            return Task.FromResult(issue.Issue);
        }
        public Task Comment(string repo, int number, string body, CancellationToken ct)
        {
            if (IssueError) throw new HttpRequestException("issues unavailable");
            if (Issues.TryGetValue(repo, out var list) && list.FirstOrDefault(i => i.Issue.Number == number) is { } issue)
                issue.Comments.Add(new(body, "main-watcher[bot]", "Bot"));
            Comments.Add(body);
            Wrote($"comment:{number}");
            return Task.CompletedTask;
        }
        public Task EditBody(string repo, int number, string body, CancellationToken ct)
        {
            if (IssueError) throw new HttpRequestException("issues unavailable");
            var issue = Find(repo, number);
            issue.Issue = issue.Issue with { Body = body };
            Wrote($"update:{number}");
            return Task.CompletedTask;
        }
        public Task Close(string repo, int number, string reason, long? duplicateOf, CancellationToken ct)
        {
            if (IssueError) throw new HttpRequestException("issues unavailable");
            var issue = Find(repo, number);
            issue.Issue = issue.Issue with { State = "closed", StateReason = reason };
            issue.ClosedBy = new("main-watcher[bot]", "Bot");
            Wrote($"close:{number}" + (reason == "completed" ? "" : $":{reason}") + (duplicateOf is null ? "" : $":{duplicateOf}"));
            return Task.CompletedTask;
        }

        public List<string> Writes { get; } = [];
        public long? DispatchId { get; init; } = 42;
        public List<WorkflowRun> RunList { get; set; } = [];
        public CtrfResult ReportResult { get; init; } = new(true, [new("Alpha", "suite", "failed")]);
        public bool JobsError { get; init; }
        public HttpRequestException? DispatchError { get; init; }
        public bool RunsError { get; init; }
        public bool InvalidCaller { get; init; }
        public string? Conclusion { get; private set; }
        public string Summary { get; private set; } = "";
        public Task ValidateTarget(Target target, CancellationToken ct) => InvalidCaller ? throw new InvalidDataException("mismatch") : Task.CompletedTask;
        public Task<string> MainHead(string repo, CancellationToken ct) => Task.FromResult("head");
        public Task<IReadOnlyList<CheckRun>> Checks(string repo, CancellationToken ct) => Task.FromResult<IReadOnlyList<CheckRun>>([]);
        public List<string> HistoryShas { get; init; } = [];
        public Dictionary<string, List<CheckRun>> CommitCheckRuns { get; init; } = [];
        public List<Push> Activity { get; init; } = [];
        public bool ActivityError { get; init; }
        public Dictionary<string, int> Counts { get; init; } = [];
        public Task<IReadOnlyList<string>> History(string repo, string sha, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(HistoryShas.Take(limit).ToArray());
        public Task<IReadOnlyList<CheckRun>> CommitChecks(string repo, string sha, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CheckRun>>(CommitCheckRuns.GetValueOrDefault(sha, []));
        public Task<IReadOnlyList<Push>> Pushes(string repo, int limit, CancellationToken ct) =>
            ActivityError ? throw new HttpRequestException("activity unavailable") : Task.FromResult<IReadOnlyList<Push>>(Activity.Take(limit).ToArray());
        public Task<int?> CommitCount(string repo, string before, string after, CancellationToken ct) =>
            Task.FromResult<int?>(Counts.TryGetValue(after, out var count) ? count : null);
        public Task<CheckRun> CreateCheck(string repo, string sha, DateTimeOffset now, CancellationToken ct) { Writes.Add("create"); return Task.FromResult(new CheckRun(1, sha, "in_progress", null, now, null, null)); }
        public Task<long?> Dispatch(string repo, string sha, long checkId, CancellationToken ct) { Writes.Add("dispatch"); if (DispatchError is not null) throw DispatchError; return Task.FromResult(DispatchId); }
        public Task<IReadOnlyList<WorkflowRun>> Runs(string repo, DateTimeOffset since, CancellationToken ct) => RunsError ? throw new HttpRequestException("unavailable") : Task.FromResult<IReadOnlyList<WorkflowRun>>(RunList);
        public Task Link(string repo, long checkId, long runId, CancellationToken ct) { Writes.Add($"link:{runId}"); return Task.CompletedTask; }
        public Task<IReadOnlyList<WorkflowJob>?> Jobs(string repo, long runId, CancellationToken ct) => JobsError ? throw new HttpRequestException("unavailable") : Task.FromResult<IReadOnlyList<WorkflowJob>?>([new("tests / main-watcher", "completed", [new("main-watcher-test", JobConclusion), new("main-watcher-tests-finished", "success")])]);
        public Task<CtrfResult> Reports(string repo, long runId, CancellationToken ct) => Task.FromResult(ReportResult);
        public Task Complete(string repo, long checkId, string conclusion, string summary, CancellationToken ct) { Order.Add($"complete:{conclusion}"); Conclusion = conclusion; Summary = summary; return Task.CompletedTask; }
    }
}

