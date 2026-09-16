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
        Assert.True(await new Reporter(fake).Report("owner/repo", Check("in_progress", null), TestContext.Current.CancellationToken));
        Assert.Equal("failure", fake.Conclusion);
        Assert.Contains("failing tests unknown", fake.Summary);
    }

    [Fact]
    public async Task ReporterLeavesJobsApiErrorsPending()
    {
        var fake = new FakeGitHub { JobsError = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake).Report("owner/repo", Check("in_progress", null), TestContext.Current.CancellationToken));
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

    sealed class FakeGitHub : IGitHubGateway
    {
        public List<string> Writes { get; } = [];
        public long? DispatchId { get; init; } = 42;
        public List<WorkflowRun> RunList { get; set; } = [];
        public CtrfResult ReportResult { get; init; } = new(true, [new("Alpha", "suite", "failed")]);
        public bool JobsError { get; init; }
        public string? Conclusion { get; private set; }
        public string Summary { get; private set; } = "";
        public Task<string> MainHead(string repo, CancellationToken ct) => Task.FromResult("head");
        public Task<IReadOnlyList<CheckRun>> Checks(string repo, CancellationToken ct) => Task.FromResult<IReadOnlyList<CheckRun>>([]);
        public Task<CheckRun> CreateCheck(string repo, string sha, DateTimeOffset now, CancellationToken ct) { Writes.Add("create"); return Task.FromResult(new CheckRun(1, sha, "in_progress", null, now, null, null)); }
        public Task<long?> Dispatch(string repo, string sha, long checkId, CancellationToken ct) { Writes.Add("dispatch"); return Task.FromResult(DispatchId); }
        public Task<IReadOnlyList<WorkflowRun>> Runs(string repo, DateTimeOffset since, CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkflowRun>>(RunList);
        public Task Link(string repo, long checkId, long runId, CancellationToken ct) { Writes.Add($"link:{runId}"); return Task.CompletedTask; }
        public Task<IReadOnlyList<WorkflowJob>?> Jobs(string repo, long runId, CancellationToken ct) => JobsError ? throw new HttpRequestException("unavailable") : Task.FromResult<IReadOnlyList<WorkflowJob>?>([new("tests / main-watcher", "completed", [new("main-watcher-test", "failure"), new("main-watcher-tests-finished", "success")])]);
        public Task<CtrfResult> Reports(string repo, long runId, CancellationToken ct) => Task.FromResult(ReportResult);
        public Task Complete(string repo, long checkId, string conclusion, string summary, CancellationToken ct) { Conclusion = conclusion; Summary = summary; return Task.CompletedTask; }
    }
}

