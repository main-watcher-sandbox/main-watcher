using System.Net;
using MainWatcher.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace MainWatcher.Worker.Tests;

/// <summary>The worker's own health alerts (ADR-012, ADR-013 point 6, R-13): TS-U7 and the conditions around it.</summary>
public class AlertTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-17T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    const string WatcherRepo = "owner/watcher";
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Health : IAccessHealth
    {
        public Dictionary<string, string> Refused { get; } = new(StringComparer.Ordinal);
        public RateLimit? Limit { get; set; }
        public IReadOnlyDictionary<string, string> TokenFailures => Refused;
        public RateLimit? TakeLowestRateLimit()
        {
            var seen = Limit;
            Limit = null;
            return seen;
        }
    }

    sealed class Setup
    {
        public FakeGitHub Doorbell { get; } = new();
        public Health Access { get; } = new();
        public DateTimeOffset Now { get; set; } = Start;
        public WorkerAlerts Alerts { get; }

        public Setup() => Alerts = new(new Alerts(Doorbell, WatcherRepo), Access, NullLogger.Instance, Start, () => Now);

        public Task Cycle(CycleObservations? observations = null, string? failure = null) =>
            Alerts.Review(observations ?? new(), failure, Ct);

        public string[] Titles => Doorbell.IssueList.Select(i => i.Title).ToArray();
        public string[] Comments(string title) =>
            Doorbell.CommentsByIssue.GetValueOrDefault(Doorbell.IssueList.Single(i => i.Title == title).Number, []).ToArray();
    }

    // TS-U7: a condition that still holds comments on the open alert instead of opening a second issue.
    [Fact]
    public async Task ARepeatedConditionCommentsOnTheOpenAlert()
    {
        var setup = new Setup();
        setup.Access.Refused["mw-observer on owner/repo"] = "HTTP 401";
        await setup.Cycle();
        Assert.Equal(["A GitHub App credential is being refused"], setup.Titles);

        // While the condition holds it is repeated at most once an hour, and never as a second issue.
        setup.Now = Start.AddMinutes(59);
        await setup.Cycle();
        Assert.Empty(setup.Comments("A GitHub App credential is being refused"));
        setup.Now = Start.Add(WorkerAlerts.Repeat);
        await setup.Cycle();
        Assert.Equal(["A GitHub App credential is being refused"], setup.Titles);
        Assert.Single(setup.Comments("A GitHub App credential is being refused"));
    }

    [Fact]
    public async Task ACredentialThatWorksAgainClearsTheConditionSoTheNextRefusalAlertsAtOnce()
    {
        var setup = new Setup();
        setup.Access.Refused["mw-doorbell on owner/watcher"] = "HTTP 404";
        await setup.Cycle();
        setup.Access.Refused.Clear();
        setup.Now = Start.AddMinutes(1);
        await setup.Cycle();
        setup.Access.Refused["mw-doorbell on owner/watcher"] = "HTTP 404";
        setup.Now = Start.AddMinutes(2);
        await setup.Cycle();
        // The second refusal is a comment on the one open alert, not a wait for the repeat interval.
        Assert.Equal(["A GitHub App credential is being refused"], setup.Titles);
        Assert.Single(setup.Comments("A GitHub App credential is being refused"));
    }

    [Fact]
    public async Task ThreeFailingCyclesInARowAlert()
    {
        var setup = new Setup();
        await setup.Cycle(failure: "the cycle failed: 502");
        await setup.Cycle(new() { Problems = ["owner/repo: checks unavailable"] });
        Assert.Empty(setup.Titles);
        await setup.Cycle(failure: "the cycle failed: 502");
        Assert.Equal(["The trigger worker's cycles keep failing"], setup.Titles);
        Assert.Contains("last 3 cycles failed", setup.Doorbell.IssueList[0].Body);
        Assert.Contains("owner/repo: checks unavailable", setup.Doorbell.IssueList[0].Body);

        // One clean cycle starts the count again.
        await setup.Cycle();
        setup.Now = Start.AddHours(2);
        await setup.Cycle(failure: "the cycle failed: 502");
        await setup.Cycle(failure: "the cycle failed: 502");
        Assert.Empty(setup.Comments("The trigger worker's cycles keep failing"));
    }

    [Fact]
    public async Task AnIdleWatcherNeverAlertsAboutUncompletedRuns()
    {
        var setup = new Setup();
        // Nothing started, so completing nothing is exactly right, however long it lasts.
        setup.Now = Start.AddHours(9);
        await setup.Cycle();
        Assert.Empty(setup.Titles);
    }

    [Fact]
    public async Task RunsStartedButNeverCompletedAlertAfterTwoHours()
    {
        var setup = new Setup();
        await setup.Cycle(new() { WatchRunStarted = true, WatchRunCompleted = true });
        setup.Now = Start.Add(WorkerAlerts.NoRunWindow).AddSeconds(-1);
        await setup.Cycle(new() { WatchRunStarted = true });
        Assert.Empty(setup.Titles);

        // Two hours after the last completion, with runs still being started.
        setup.Now = Start.Add(WorkerAlerts.NoRunWindow);
        await setup.Cycle(new() { WatchRunStarted = true });
        Assert.Equal(["No `watch.yml` run has completed in 2 hours"], setup.Titles);

        setup.Now = setup.Now.AddMinutes(1);
        await setup.Cycle(new() { WatchRunStarted = true, WatchRunCompleted = true });
        setup.Now = setup.Now.AddMinutes(1);
        await setup.Cycle(new() { WatchRunStarted = true });
        Assert.Empty(setup.Comments("No `watch.yml` run has completed in 2 hours"));
    }

    [Theory]
    [InlineData(999, 5000, true)]
    [InlineData(1000, 5000, false)]
    public async Task ARateLimitBelowAFifthAlerts(int remaining, int limit, bool alerts)
    {
        var setup = new Setup();
        setup.Access.Limit = new("core", remaining, limit, Start.AddMinutes(30));
        await setup.Cycle();
        Assert.Equal(alerts ? ["GitHub rate limit below 20%"] : [], setup.Titles);
        if (alerts) Assert.Contains($"Only {remaining} of {limit} `core` requests are left (20%", setup.Doorbell.IssueList[0].Body);
    }

    [Fact]
    public async Task ACycleThatReachedNoRateLimitHeaderLeavesTheConditionAlone()
    {
        var setup = new Setup();
        setup.Access.Limit = new("core", 1, 5000, null);
        await setup.Cycle();
        // No headers this cycle: the last reading still stands, so nothing is raised and nothing is cleared.
        setup.Now = Start.Add(WorkerAlerts.Repeat);
        await setup.Cycle();
        Assert.Single(setup.Titles);
        Assert.Empty(setup.Comments("GitHub rate limit below 20%"));
    }

    static CycleObservations Owed(DateTimeOffset since) => new()
    {
        Examined = ["owner/repo"],
        Pending = [new("owner/repo", 7, "check 7: the main-watcher job of target run 41 has completed", since)]
    };

    // ADR-013 point 6: reporting pending is timed from the test job, so it survives a worker restart.
    [Fact]
    public async Task AReportPendingForMoreThanFifteenMinutesAlerts()
    {
        var setup = new Setup();
        var completed = Start.AddMinutes(-14);
        await setup.Cycle(Owed(completed));
        Assert.Empty(setup.Titles);

        setup.Now = Start.AddMinutes(1);
        await setup.Cycle(Owed(completed));
        Assert.Equal(["Reporting pending on owner/repo"], setup.Titles);
        Assert.Contains("A report has been owed on `owner/repo`", setup.Doorbell.IssueList[0].Body);
        Assert.Contains("(15 minutes)", setup.Doorbell.IssueList[0].Body);

        // Once the Reporter catches up, the condition clears without a second alert.
        setup.Now = Start.AddMinutes(2);
        await setup.Cycle(new() { Examined = ["owner/repo"] });
        setup.Now = Start.AddHours(3);
        await setup.Cycle(new() { Examined = ["owner/repo"] });
        Assert.Empty(setup.Comments("Reporting pending on owner/repo"));
    }

    [Fact]
    public async Task WithoutAJobTimeTheWorkerDatesThePendingReportItself()
    {
        var setup = new Setup();
        // A deleted run has no job to date the report, so the first cycle that saw it owed starts the clock.
        await setup.Cycle(new() { Examined = ["owner/repo"], Pending = [new("owner/repo", 7, "target run 41 was deleted", default)] });
        setup.Now = Start.Add(WorkerAlerts.ReportPendingAfter).AddSeconds(-1);
        await setup.Cycle(new() { Examined = ["owner/repo"], Pending = [new("owner/repo", 7, "target run 41 was deleted", default)] });
        Assert.Empty(setup.Titles);
        setup.Now = Start.Add(WorkerAlerts.ReportPendingAfter);
        await setup.Cycle(new() { Examined = ["owner/repo"], Pending = [new("owner/repo", 7, "target run 41 was deleted", default)] });
        Assert.Equal(["Reporting pending on owner/repo"], setup.Titles);
    }

    [Fact]
    public async Task ATargetSkippedForItsOwnRunningCycleKeepsItsPendingClock()
    {
        var setup = new Setup();
        // A deleted run, so only the worker's own clock dates the report.
        CycleObservations Deleted() => new()
        {
            Examined = ["owner/repo"],
            Pending = [new("owner/repo", 7, "target run 41 was deleted", default)]
        };
        await setup.Cycle(Deleted());
        // The next cycle skips the target because its watch.yml run is queued, so nothing examined it: the clock keeps running.
        setup.Now = Start.AddMinutes(10);
        await setup.Cycle(new() { WatchRunStarted = true });
        setup.Now = Start.Add(WorkerAlerts.ReportPendingAfter);
        await setup.Cycle(Deleted());
        Assert.Equal(["Reporting pending on owner/repo"], setup.Titles);
    }

    [Fact]
    public async Task EachStuckTargetGetsItsOwnAlert()
    {
        var setup = new Setup();
        setup.Now = Start.AddHours(1);
        await setup.Cycle(new()
        {
            Examined = ["owner/a", "owner/b"],
            Pending = [new("owner/a", 7, "run 41 completed", Start), new("owner/b", 8, "run 42 completed", Start)]
        });
        Assert.Equal(["Reporting pending on owner/a", "Reporting pending on owner/b"], setup.Titles);
    }

    [Fact]
    public async Task AnAlertThatCannotBeRaisedIsTriedAgainOnTheNextCycle()
    {
        var setup = new Setup();
        setup.Doorbell.IssueWritesFail = true;
        setup.Access.Refused["mw-observer on owner/repo"] = "HTTP 401";
        // Never a required write: the review returns, and the worker keeps cycling.
        await setup.Cycle();
        Assert.Empty(setup.Titles);

        setup.Doorbell.IssueWritesFail = false;
        setup.Now = Start.AddMinutes(1);
        await setup.Cycle();
        Assert.Equal(["A GitHub App credential is being refused"], setup.Titles);
    }

    [Fact]
    public async Task TheRateLimitWatchReadsGitHubsHeaders()
    {
        var watch = new RateLimitWatch { InnerHandler = new Responder() };
        using var http = new HttpClient(watch) { BaseAddress = new("https://api.github.com/") };
        await http.GetAsync("a?remaining=4000&limit=5000&resource=core", Ct);
        await http.GetAsync("b?remaining=10&limit=1000&resource=graphql", Ct);
        await http.GetAsync("c?remaining=900&limit=1000&resource=core", Ct);

        // The lowest budget of any response wins, and reading it forgets it.
        var lowest = watch.Take();
        Assert.Equal("graphql", lowest!.Resource);
        Assert.Equal(0.01, lowest.Left, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789500000), lowest.Reset);
        Assert.Null(watch.Take());

        // A response without the headers says nothing about the budget.
        await http.GetAsync("d", Ct);
        Assert.Null(watch.Take());
    }

    sealed class Responder : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            var query = request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
            if (query.TryGetValue("remaining", out var remaining))
            {
                response.Headers.Add("x-ratelimit-remaining", remaining);
                response.Headers.Add("x-ratelimit-limit", query["limit"]);
                response.Headers.Add("x-ratelimit-resource", query["resource"]);
                response.Headers.Add("x-ratelimit-reset", "1789500000");
            }
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void ABudgetWithNoLimitCountsAsFull()
    {
        Assert.Equal(1, new RateLimit("core", 5000, 5000, null).Left);
        // A response GitHub does not rate-limit says nothing, so it must never read as exhausted.
        Assert.Equal(1, new RateLimit("core", 0, 0, null).Left);
    }
}
