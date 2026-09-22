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

    // ADR-011: wall clock spans every report's summary, summed time adds up the tests, and the slowest are this run's.
    [Fact]
    public void ReportsGiveSuiteTimeSlowestTestsAndRetries()
    {
        var first = JsonNode.Parse(Fixture("project-0.json"))!;
        var second = JsonNode.Parse(Fixture("failed-project.json"))!;
        var start = first["results"]!["summary"]!["start"]!.GetValue<long>();
        first["results"]!["summary"]!["stop"] = start + 20_000;
        second["results"]!["summary"]!["start"] = start + 5_000;
        second["results"]!["summary"]!["stop"] = start + 26_500;
        var tests = first["results"]!["tests"]!.AsArray().Concat(second["results"]!["tests"]!.AsArray()).ToArray();
        for (var i = 0; i < tests.Length; i++)
        {
            tests[i]!["duration"] = (i + 1) * 100;
            tests[i]!.AsObject().Remove("retries");
        }
        tests[^1]!["duration"] = 20_100;
        tests[^1]!["retries"] = 1;

        var timing = CtrfReader.Read([first.ToJsonString(), second.ToJsonString()]).Timing!;
        Assert.Equal(26_500, timing.WallClockMs);
        Assert.Equal(Enumerable.Range(1, tests.Length - 1).Sum(i => i * 100L) + 20_100, timing.SummedMs);
        Assert.Equal(1, timing.Retried);
        Assert.Equal(CtrfReader.SlowestCount, timing.Slowest.Count);
        Assert.Equal(tests[^1]!["name"]!.GetValue<string>(), timing.Slowest[0].Name);
        Assert.True(timing.Slowest[0].Retried);
        Assert.Equal(timing.Slowest.Select(t => t.DurationMs).OrderDescending(), timing.Slowest.Select(t => t.DurationMs));
        Assert.Null(CtrfReader.Read([]).Timing);
    }

    // R-17: the watcher's own conversion of CTRF milliseconds, checked against what TS-S13 saw for xUnit v3's values.
    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(136, "136 ms")]
    [InlineData(2_100, "2.1 s")]
    [InlineData(999, "999 ms")]
    [InlineData(20_091, "20.1 s")]
    [InlineData(20_149, "20.1 s")]
    [InlineData(59_949, "59.9 s")]
    [InlineData(59_950, "1 min 0 s")]
    [InlineData(125_300, "2 min 5 s")]
    public void DurationsAreConvertedFromMilliseconds(long ms, string shown) => Assert.Equal(shown, TimingSection.Duration(ms));

    static readonly CtrfResult Timed = new(true, [new("Alpha", "suite", "failed")],
        new(20_200, 22_300, 1, [new("Tests.Slow", 20_100, false), new("Tests.Flaky|pipe", 2_100, true), new("Tests.Fast", 136, false)]));

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    public async Task CheckRunShowsTimingAgainstTheLastGreenRun(string conclusion)
    {
        var ct = TestContext.Current.CancellationToken;
        CheckRun Green(long id, int hours, string? summary) =>
            new(id, $"green{id}", "completed", "success", Now.AddHours(-hours), Now.AddHours(-hours).AddMinutes(5), "1", "Tests passed", summary);
        var fake = new FakeGitHub
        {
            JobConclusion = conclusion,
            ReportResult = Timed,
            CheckList =
            [
                Green(5, 5, "<!-- main-watcher suite_ms=10000 -->"),
                Green(6, 2, "Passed.\n\n<!-- main-watcher suite_ms=19000 -->"),
                new(7, "red", "completed", "failure", Now.AddHours(-1.5), Now, "1", "Tests failed", "<!-- main-watcher suite_ms=1 -->"),
                // Started after this run, so it is no earlier result to compare with.
                Green(8, 0, "<!-- main-watcher suite_ms=1 -->"),
            ],
        };
        await new Reporter(fake, clock: () => Now).Report(new() { Repo = "owner/repo" }, Check("in_progress", null), ct);

        Assert.Equal(conclusion, fake.Conclusion);
        Assert.Contains("- Suite time: 20.2 s wall clock; 22.3 s summed across tests", fake.Summary);
        Assert.Contains("- Change from the last green run: +1.2 s (+6.3%) against 19.0 s at [`green6`]", fake.Summary);
        Assert.Contains("- Retried: yes, 1 failed test was run a second time", fake.Summary);
        Assert.Contains("| Tests.Slow | 20.1 s |\n| Tests.Flaky\\|pipe (retried) | 2.1 s |\n| Tests.Fast | 136 ms |", fake.Summary);
        Assert.Equal("20200", Markers.Field(fake.Summary, TimingSection.SuiteMs));
        // The section precedes the lock details, so a long failure list cannot truncate it away.
        if (conclusion == "failure") Assert.True(fake.Summary.IndexOf("**Timing**") < fake.Summary.IndexOf("Lock issue"));
    }

    [Fact]
    public async Task TimingSaysWhenThereIsNothingToCompareWith()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { JobConclusion = "success", ReportResult = Timed with { Failures = [] } };
        await new Reporter(fake).Report(new() { Repo = "owner/repo" }, Check("in_progress", null), ct);
        Assert.Contains("- Change from the last green run: none to compare with", fake.Summary);

        // A green run from before this section existed recorded no time.
        var old = new FakeGitHub
        {
            JobConclusion = "success", ReportResult = Timed with { Failures = [] },
            CheckList = [new(5, "old", "completed", "success", Now.AddHours(-3), Now, "1", "Tests passed", "Passed.")],
        };
        await new Reporter(old).Report(new() { Repo = "owner/repo" }, Check("in_progress", null), ct);
        Assert.Contains("unknown, because the last green run, [`old`](https://github.com/owner/repo/commit/old), recorded no suite time", old.Summary);
    }

    [Fact]
    public async Task UnreadableCheckRunsOrReportsNeverStopTheReport()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { JobConclusion = "success", ReportResult = Timed with { Failures = [] }, ChecksError = true };
        Assert.True(await new Reporter(fake).Report(new() { Repo = "owner/repo" }, Check("in_progress", null), ct));
        Assert.Equal("success", fake.Conclusion);
        Assert.Contains("- Change from the last green run: unknown, because the earlier check runs could not be read (checks unavailable)", fake.Summary);
        Assert.Contains("- Suite time: 20.2 s", fake.Summary);

        var unknown = new FakeGitHub { JobConclusion = "success", ReportResult = new(true, []), ChecksError = true };
        await new Reporter(unknown).Report(new() { Repo = "owner/repo" }, Check("in_progress", null), ct);
        Assert.Contains("Timings unknown: the CTRF reports could not be read.", unknown.Summary);
        Assert.Null(Markers.Field(unknown.Summary, TimingSection.SuiteMs));
    }

    // TS-U13 and TS-U3: the shared rule, read from the fixtures the trigger worker's tests also use.
    [Theory]
    [MemberData(nameof(EligibilityFixtures.Names), true, MemberType = typeof(EligibilityFixtures))]
    public void EligibilityRule(string name)
    {
        var c = EligibilityFixtures.Named(name);
        Assert.Equal(c.Eligible, Eligibility.CanStart(c.Head, c.Checks, c.PollInterval, c.Now, c.Force));
        // The cap is a property of the head alone: a forced dispatch starts a capped head without lifting the cap.
        Assert.Equal(c.Capped, Eligibility.Capped(c.Head, c.Checks));
    }

    // TS-U13: the Planner starts exactly the heads the rule allows, and raises "head untestable" for exactly the capped ones.
    [Theory]
    [MemberData(nameof(EligibilityFixtures.Names), true, MemberType = typeof(EligibilityFixtures))]
    public async Task PlannerStartsOnlyEligibleHeads(string name)
    {
        var c = EligibilityFixtures.Named(name);
        var fake = new FakeGitHub { CheckList = c.Checks.ToList() };
        var watcher = new FakeGitHub();
        var planner = new Planner(fake, () => c.Now, alerts: new Alerts(watcher, "owner/watcher"));
        var started = await planner.Plan(new() { Repo = "owner/repo", PollInterval = (int)c.PollInterval.TotalMinutes },
            c.Force, TestContext.Current.CancellationToken);
        Assert.Equal(c.Eligible, started is not null);
        Assert.Equal(c.Eligible ? ["create", "dispatch", "link:42"] : [], fake.Writes);
        Assert.Empty(planner.AlertFailures);
        Assert.Equal(c.Capped && !c.Force ? ["create:owner/watcher"] : [], watcher.Order);
    }

    [Fact]
    public async Task UntestableHeadNamesTheOpenLockAndIsRaisedOncePerHead()
    {
        var ct = TestContext.Current.CancellationToken;
        var capped = EligibilityFixtures.Named("head neutral three times reaches the cap");
        var fake = new FakeGitHub { CheckList = capped.Checks.ToList() };
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken: tests failed on head", "main-watcher[bot]", "Bot");
        // Not a lock the gate enforces, so not a lock the alert should name.
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken: opened by hand", "alice", "User");
        var watcher = new FakeGitHub();
        var target = new Target { Repo = "owner/repo", PollInterval = (int)capped.PollInterval.TotalMinutes };
        var planner = new Planner(fake, () => capped.Now, alerts: new Alerts(watcher, "owner/watcher"));
        Assert.Null(await planner.Plan(target, false, ct));
        var alert = watcher.Issues["owner/watcher"].Single();
        Assert.Equal("Head untestable on owner/repo", alert.Issue.Title);
        Assert.Contains("3 `neutral` check runs", alert.Issue.Body);
        Assert.Contains("- #1 (https://github.com/owner/repo/issues/1)", alert.Issue.Body);
        Assert.DoesNotContain("#2", alert.Issue.Body);
        Assert.Contains("<!-- main-watcher untestable sha=head -->", alert.Issue.Body);

        // A later cycle on the same head says nothing more; a head that runs out afterwards comments on the same alert.
        Assert.Null(await new Planner(fake, () => capped.Now, alerts: new Alerts(watcher, "owner/watcher")).Plan(target, false, ct));
        Assert.Equal(["create:owner/watcher"], watcher.Order);
        var next = new FakeGitHub { CheckList = capped.Checks.Select(r => r with { Sha = "next" }).ToList(), Head = "next" };
        Assert.Null(await new Planner(next, () => capped.Now, alerts: new Alerts(watcher, "owner/watcher")).Plan(target, false, ct));
        Assert.Equal(["create:owner/watcher", "comment:1"], watcher.Order);
        Assert.Contains("No lock is open", watcher.Comments.Single());
    }

    [Fact]
    public async Task UntestableAlertFailureIsReportedAndNeverThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var capped = EligibilityFixtures.Named("head neutral three times reaches the cap");
        var fake = new FakeGitHub { CheckList = capped.Checks.ToList() };
        var target = new Target { Repo = "owner/repo", PollInterval = (int)capped.PollInterval.TotalMinutes };
        var planner = new Planner(fake, () => capped.Now, alerts: new Alerts(new FakeGitHub { IssueError = true }, "owner/watcher"));
        Assert.Null(await planner.Plan(target, false, ct));
        Assert.Equal(["Head untestable on owner/repo: issues unavailable"], planner.AlertFailures);

        var sinkless = new Planner(fake, () => capped.Now);
        Assert.Null(await sinkless.Plan(target, false, ct));
        Assert.Equal(["Head untestable on owner/repo: no alert sink configured"], sinkless.AlertFailures);
    }

    // ADR-014: a cycle that processes a target renews its open App locks' leases, and touches nothing else.
    [Fact]
    public async Task RenewalSetsTheLeaseOnEveryOpenAppLockAndOnNothingElse()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub();
        var body = Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddMinutes(30))));
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot", body);
        // Neither of these is a lock the gate enforces, so neither is a lease to keep alive.
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken: opened by hand", "alice", "User", body);
        var closed = fake.Seed("owner/repo", Reporter.LockLabel, "an older lock", "main-watcher[bot]", "Bot", body);
        closed.Issue = closed.Issue with { State = "closed", StateReason = "completed" };

        var lines = await new Planner(fake, () => Now).Renew(new() { Repo = "owner/repo" }, ct);
        Assert.Equal(["update:1"], fake.Order);
        Assert.Equal(Now + Lease.Default, MainWatcher.Gate.LockLease.ReadLeaseUntil(fake.Find("owner/repo", 1).Body));
        Assert.Contains("lease renewed until 2026-09-16T23:00:00Z", lines.Single());
        // A lease that had not run out is no lapse, so nothing is recorded and nothing is reported.
        Assert.Null(Markers.Field(fake.Find("owner/repo", 1).Body, Lease.Lapsed));
        Assert.Empty(fake.Comments);
    }

    [Fact]
    public async Task RenewalUsesTheConfiguredLockLeaseAndWritesOneEvenWhereTheMarkerIsMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub();
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot", "Locked, with no marker.");
        var target = TargetConfiguration.Parse("lock_lease: 10\ntargets:\n  - repo: owner/repo").Targets.Single();
        await new Planner(fake, () => Now).Renew(target, ct);
        var body = fake.Find("owner/repo", 1).Body;
        Assert.Equal(Now.AddMinutes(10), MainWatcher.Gate.LockLease.ReadLeaseUntil(body));
        // Nothing says since when the gate has been failing open, so no window is invented and no lapse is reported.
        Assert.Null(Markers.Field(body, Lease.Lapsed));
        Assert.Equal(["update:1"], fake.Order);
    }

    // ADR-014 point 4: the lapse is written with the new lease, then commented, alerted and marked reported.
    [Fact]
    public async Task RenewingAnExpiredLeaseRecordsTheLapseThenReportsItOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var expiry = Now.AddMinutes(-90);
        var fake = new FakeGitHub();
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot",
            Markers.Set("Locked.", (Lease.Until, Markers.Stamp(expiry))));
        var watcher = new FakeGitHub();
        var planner = new Planner(fake, () => Now, alerts: new Alerts(watcher, "owner/watcher"));

        await planner.Renew(new() { Repo = "owner/repo" }, ct);
        var body = fake.Find("owner/repo", 1).Body;
        Assert.Equal($"{Markers.Stamp(expiry)}..{Markers.Stamp(Now)}", Markers.Field(body, Lease.Lapsed));
        Assert.Equal(Now, Markers.Time(body, QueueSweep.Required));
        Assert.Equal(Now + Lease.Default, MainWatcher.Gate.LockLease.ReadLeaseUntil(body));
        Assert.Equal(Now, Markers.Time(body, Lease.Reported));
        Assert.Equal(["update:1", "comment:1", "update:1"], fake.Order);
        Assert.Contains("90 minutes later", fake.Comments.Single());
        Assert.Contains($"<!-- main-watcher lapsed={Markers.Stamp(expiry)}..{Markers.Stamp(Now)} -->", fake.Comments.Single());
        var alert = watcher.Issues["owner/watcher"].Single();
        Assert.Equal("Lock lease lapsed on owner/repo", alert.Issue.Title);
        Assert.Contains("https://github.com/owner/repo/issues/1", alert.Issue.Body);

        // The next cycle renews a lease that is valid: no second lapse, no second comment and no second alert.
        await planner.Renew(new() { Repo = "owner/repo" }, ct);
        Assert.Equal(["update:1", "comment:1", "update:1", "update:1"], fake.Order);
        Assert.Single(fake.Comments);
        Assert.Single(watcher.Issues["owner/watcher"]);
    }

    // A cycle stopped between the renewal and its report leaves the lapse recorded and still owed.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ALapseInterruptedAfterAnyWriteIsReportedExactlyOnce(int stopAfter)
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { StopAfterWrites = stopAfter };
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot",
            Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddMinutes(-90)))));
        var watcher = new FakeGitHub();
        Planner Cycle() => new(fake, () => Now, alerts: new Alerts(watcher, "owner/watcher"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Cycle().Renew(new() { Repo = "owner/repo" }, ct));
        Assert.Null(Markers.Time(fake.Find("owner/repo", 1).Body, Lease.Reported));
        fake.StopAfterWrites = null;

        await Cycle().Renew(new() { Repo = "owner/repo" }, ct);
        Assert.Single(fake.Comments);
        Assert.Single(watcher.Issues["owner/watcher"]);
        Assert.Equal(Now, Markers.Time(fake.Find("owner/repo", 1).Body, Lease.Reported));
    }

    // A renewal that died before reporting, followed by a second lapse, must lose neither window (PR #52 review).
    [Fact]
    public async Task ASecondLapseKeepsTheWindowTheFirstOneNeverReported()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = Now.AddMinutes(-90);
        var fake = new FakeGitHub { StopAfterWrites = 1 };
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot",
            Markers.Set("Locked.", (Lease.Until, Markers.Stamp(first))));
        var watcher = new FakeGitHub();
        var target = new Target { Repo = "owner/repo" };

        // The renewal records the lapse and the cycle dies before its comment.
        var died = Now;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new Planner(fake, () => died, alerts: new Alerts(watcher, "owner/watcher")).Renew(target, ct));
        Assert.Empty(fake.Comments);
        fake.StopAfterWrites = null;

        // Nothing runs again until the lease that renewal wrote has itself run out.
        var later = Now + Lease.Default + TimeSpan.FromMinutes(30);
        await new Planner(fake, () => later, alerts: new Alerts(watcher, "owner/watcher")).Renew(target, ct);

        var body = fake.Find("owner/repo", 1).Body;
        Assert.Equal([new Lease.Lapse(first, died), new Lease.Lapse(died + Lease.Default, later)], Lease.Windows(body));
        Assert.Empty(Lease.Unreported(body));
        Assert.Equal(later, Markers.Time(body, Lease.Reported));
        // One comment per window, each keyed to its own, and the alert names both.
        Assert.Equal(2, fake.Comments.Count);
        Assert.Contains($"ran out at {Markers.Stamp(first)}", fake.Comments[0]);
        Assert.Contains($"ran out at {Markers.Stamp(died + Lease.Default)}", fake.Comments[1]);
        Assert.Single(watcher.Issues["owner/watcher"]);
        Assert.Single(watcher.Comments);
    }

    // A green run reports and closes before the renewal runs, so the debt outlives the open lock (PR #52 review).
    [Fact]
    public async Task ALapseOwedByALockThatHasClosedIsStillReported()
    {
        var ct = TestContext.Current.CancellationToken;
        var lapse = $"{Markers.Stamp(Now.AddMinutes(-90))}..{Markers.Stamp(Now.AddMinutes(-60))}";
        var fake = new FakeGitHub();
        var closed = fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot",
            Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddHours(3))), (Lease.Lapsed, lapse)));
        closed.Issue = closed.Issue with { State = "closed", StateReason = "completed" };
        var watcher = new FakeGitHub();

        var lines = await new Planner(fake, () => Now, alerts: new Alerts(watcher, "owner/watcher"))
            .Renew(new() { Repo = "owner/repo" }, ct);

        Assert.Contains("reported 1 lapse(s) it closed still owing", lines.Single());
        Assert.Contains("ran out at", fake.Comments.Single());
        // The lock is closed, so saying it is enforced again would be false (PR #52 review).
        Assert.DoesNotContain("enforced again", fake.Comments.Single());
        Assert.Contains("closed since, so nothing is enforcing it now", fake.Comments.Single());
        Assert.Contains("closed since, so nothing is enforcing it now", watcher.Issues["owner/watcher"].Single().Body);
        Assert.Single(watcher.Issues["owner/watcher"]);
        // A closed lock needs no lease, so the only body write is the one that marks the debt paid.
        Assert.Equal(["comment:1", "update:1"], fake.Order);
        var body = fake.Find("owner/repo", 1).Body;
        Assert.Empty(Lease.Unreported(body));
        Assert.Equal(Now.AddHours(3), MainWatcher.Gate.LockLease.ReadLeaseUntil(body));
    }

    [Fact]
    public async Task AClosedLockWithNothingOwedIsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub();
        var closed = fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot",
            Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddMinutes(-90)))));
        closed.Issue = closed.Issue with { State = "closed", StateReason = "completed" };
        Assert.Empty(await new Planner(fake, () => Now).Renew(new() { Repo = "owner/repo" }, ct));
        // No lease is renewed on a closed lock: a lock that is not enforced needs no lease, and it is no lapse either.
        Assert.Empty(fake.Order);
    }

    // The marker is bounded, but a bound that dropped an entry would lose a lapse nobody had reported (PR #52 review).
    [Fact]
    public void TheLapseMarkerCoalescesOverflowRatherThanDroppingIt()
    {
        var lapses = Enumerable.Range(0, Lease.MaxWindows + 5)
            .Select(i => new Lease.Lapse(Now.AddHours(i), Now.AddHours(i).AddMinutes(1))).ToArray();
        var kept = Lease.Windows(Markers.Set("Locked.", (Lease.Lapsed, Lease.Field(lapses))));

        Assert.Equal(Lease.MaxWindows, kept.Count);
        // Nothing is lost: the oldest six became one span that still covers every moment they recorded, and says so.
        Assert.Equal(new Lease.Lapse(lapses[0].From, lapses[5].At, 6), kept[0]);
        Assert.Equal(lapses.TakeLast(Lease.MaxWindows - 1), kept.Skip(1));
        Assert.Equal(lapses[0].From, kept.Min(l => l.From));
        Assert.Equal(lapses[^1].At, kept.Max(l => l.At));
        Assert.Equal(lapses.Length, kept.Sum(l => l.Count));
        // A coalesced span survives a round trip, and an unreadable entry is skipped rather than taking the rest with it.
        Assert.Equal(kept, Lease.Windows(Markers.Set("Locked.", (Lease.Lapsed, Lease.Field(kept)))));
        Assert.Equal([lapses[0]],
            Lease.Windows(Markers.Set("Locked.", (Lease.Lapsed, $"{Lease.Field([lapses[0]])},not..a-window,{Markers.Stamp(Now)}"))));
    }

    // A span the marker coalesced stands for several lapses with enforced stretches between them, so it must not be
    // described as one unbroken window (PR #52 review).
    [Fact]
    public async Task ACoalescedSpanSaysHowManyLapsesItStandsFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub();
        var span = new Lease.Lapse(Now.AddHours(-9), Now.AddHours(-2), 4);
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot",
            Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddHours(3))), (Lease.Lapsed, Lease.Field([span]))));
        var watcher = new FakeGitHub();

        await new Planner(fake, () => Now, alerts: new Alerts(watcher, "owner/watcher")).Renew(new() { Repo = "owner/repo" }, ct);

        var comment = fake.Comments.Single();
        Assert.Contains($"ran out and was renewed 4 times between {Markers.Stamp(span.From)} and {Markers.Stamp(span.At)}", comment);
        Assert.Contains("recorded together and this window covers them all", comment);
        Assert.DoesNotContain("minutes later", comment);
        Assert.Contains("The lock is enforced again now.", comment);
    }

    [Fact]
    public async Task ALapseCannotBeReportedWithoutAnAlertSink()
    {
        var fake = new FakeGitHub();
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot",
            Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddMinutes(-1)))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Planner(fake, () => Now).Renew(new() { Repo = "owner/repo" }, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The renewal rule the trigger worker reads (TS-U5 (d)). <paramref name="until"/> and <paramref name="dueAt"/> are
    /// minutes from <see cref="Now"/>, and a null <paramref name="until"/> is an issue body with no lease marker.
    /// </summary>
    [Theory]
    // A four-hour lease: renewal is wanted once it is an hour old, counted from the renewal rather than from now.
    [InlineData(240, 240, false, 60)]
    [InlineData(240, 181, false, 1)]
    [InlineData(240, 180, true, 0)]
    [InlineData(240, -1, true, -181)]
    // Below two hours, half the lease comes first, so a renewal is asked for before the gate could stop enforcing the lock.
    [InlineData(10, 6, false, 1)]
    [InlineData(10, 5, true, 0)]
    [InlineData(120, 61, false, 1)]
    // Missing, unreadable, or further ahead than a renewal could have set it: renew now, and date nothing.
    [InlineData(240, null, true, null)]
    [InlineData(240, 241, true, null)]
    public void LeaseRenewalRule(int lockLease, int? until, bool due, int? dueAt)
    {
        var lease = TimeSpan.FromMinutes(lockLease);
        var body = until is null ? "Locked." : Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddMinutes(until.Value))));
        Assert.Equal(due, Lease.RenewalDue(body, lease, Now));
        Assert.Equal(dueAt is null ? null : Now.AddMinutes(dueAt.Value), Lease.Due(body, lease, Now));
        Assert.True(Lease.RenewalDue(Markers.Set("Locked.", (Lease.Until, "not a time")), lease, Now));
    }

    // TS-U12 (ADR-016): a sweep is owed whenever queue_swept is missing or older than the generation sweep_required names.
    [Theory]
    // No generation was ever recorded, so nothing is owed.
    [InlineData(null, null, null)]
    [InlineData(null, 0, null)]
    // Recorded and never swept, or swept for an older generation: the crash right after a lease renewal (TS-S17 (b)).
    [InlineData(0, null, 0)]
    [InlineData(0, -30, 0)]
    // Swept for this generation, or a later one, so nothing is left.
    [InlineData(0, 0, null)]
    [InlineData(-30, 0, null)]
    public void ASweepIsOwedUntilQueueSweptCatchesUpWithItsGeneration(int? required, int? swept, int? owed)
    {
        (string Name, string Value)[] fields = [
            .. required is { } r ? new[] { (QueueSweep.Required, Markers.Stamp(Now.AddMinutes(r))) } : [],
            .. swept is { } s ? new[] { (QueueSweep.Swept, Markers.Stamp(Now.AddMinutes(s))) } : []];
        Assert.Equal(owed is { } o ? Now.AddMinutes(o) : null, QueueSweep.Owed(Markers.Set("Locked.", fields)));
        // Only Main Watcher writes these markers: one that cannot be read names no generation to sweep for, and treating it
        // as owed would owe a sweep that nothing could discharge.
        Assert.Null(QueueSweep.Owed(Markers.Set("Locked.", (QueueSweep.Required, "not a time"))));
    }

    // TS-U12: which gate runs of a queued group the sweep re-runs, including the 5-minute margin on the cut-off.
    [Theory]
    // Passed before the cut-off: the group is waiting on a verdict taken without the lock.
    [InlineData("completed", "success", -1, GateRunHandling.Rerun)]
    // Inside the margin, which absorbs clock differences and the delay before a new lock is visible.
    [InlineData("completed", "success", 4, GateRunHandling.Rerun)]
    [InlineData("completed", "success", 5, GateRunHandling.Leave)]
    // Still running: it may yet report "no lock", so it is re-run once it completes.
    [InlineData("in_progress", null, -1, GateRunHandling.Wait)]
    [InlineData("queued", null, -1, GateRunHandling.Wait)]
    // Started after the cut-off, so it read the lock itself.
    [InlineData("in_progress", null, 6, GateRunHandling.Leave)]
    // Nothing that did not pass is holding the group's merge open.
    [InlineData("completed", "failure", -1, GateRunHandling.Leave)]
    [InlineData("completed", "cancelled", -1, GateRunHandling.Leave)]
    public void TheSweepRerunsOnlyGatesThatPassedBeforeTheCutoff(string status, string? conclusion, int minutes, GateRunHandling handling) =>
        Assert.Equal(handling, QueueSweep.Handle(new(9001, status, conclusion, Now.AddMinutes(minutes)), QueueSweep.Cutoff(Now)));

    /// <summary>A lock owing a sweep for <paramref name="minutesAgo"/> ago, swept to <paramref name="swept"/> if at all.</summary>
    static FakeIssue SeedSweep(FakeGitHub fake, int minutesAgo, int? swept = null) =>
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot",
            Markers.Set("Locked.", [(QueueSweep.Required, Markers.Stamp(Now.AddMinutes(-minutesAgo))),
                .. swept is { } s ? new[] { (QueueSweep.Swept, Markers.Stamp(Now.AddMinutes(-s))) } : []]));

    /// <summary>A group in the queue for pull request <paramref name="pull"/>, whose gate ran on commit <c>group-&lt;pull&gt;</c>.</summary>
    static QueuedGroup Group(int pull) => new($"gh-readonly-queue/main/pr-{pull}-{Sha('a')}", $"group-{pull}", pull);

    // TS-U12: the queue sweep re-runs the gate of every group still queued whose gate decided before the lock, leaves the
    // rest alone, and records the generation done.
    [Fact]
    public async Task TheSweepRerunsGatesTakenBeforeTheLockAndThenRecordsItDone()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { Rerunning = Now };
        // The lock opened 20 minutes ago, so gate runs that started before 15 minutes ago are suspect.
        SeedSweep(fake, 20);
        fake.Queued.AddRange([Group(12), Group(13), Group(14)]);
        // #12 passed its gate while no lock existed: this is the group ADR-016 exists for.
        fake.Gates["group-12"] = [new(9012, "completed", "success", Now.AddMinutes(-40))];
        // #13's gate started well after the cut-off, so it read the lock itself.
        fake.Gates["group-13"] = [new(9013, "completed", "success", Now.AddMinutes(-10))];
        // #14's gate already failed, so nothing it did is holding a merge open.
        fake.Gates["group-14"] = [new(9014, "completed", "failure", Now.AddMinutes(-40))];
        // A group that left the queue is not swept: its commit is never asked about.
        fake.Gates["group-99"] = [new(9099, "completed", "success", Now.AddMinutes(-40))];

        var lines = await new Planner(fake, () => Now).SweepQueue(Locked, null, ct);

        Assert.Equal(["rerun:9012", "update:1"], fake.Order);
        Assert.DoesNotContain("gates:group-99", fake.Reads);
        Assert.Equal(Now.AddMinutes(-20), Markers.Time(fake.Find("owner/repo", 1).Body, QueueSweep.Swept));
        Assert.Contains("re-ran 1 gate run(s) — #12 (gate run 9012)", lines.Single());
        Assert.Contains("complete.", lines.Single());

        // Nothing is owed now, so a second cycle reads no queue and asks for no re-run.
        fake.Reads.Clear();
        Assert.Empty(await new Planner(fake, () => Now).SweepQueue(Locked, null, ct));
        Assert.Equal(["rerun:9012", "update:1"], fake.Order);
        Assert.Empty(fake.Reads);
    }

    // TS-U12: a gate still running may yet report "no lock", so the sweep stays owed until it completes, and re-runs it then.
    [Fact]
    public async Task AGateStillRunningKeepsTheSweepOwedUntilItCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { Rerunning = Now };
        SeedSweep(fake, 2);
        fake.Queued.Add(Group(12));
        fake.Gates["group-12"] = [new(9012, "in_progress", null, Now.AddMinutes(-20))];

        var lines = await new Planner(fake, () => Now).SweepQueue(Locked, null, ct);
        Assert.Empty(fake.Order);
        Assert.Null(Markers.Time(fake.Find("owner/repo", 1).Body, QueueSweep.Swept));
        Assert.Contains("gate run 9012 for #12 is still running", lines.Single());

        // It finishes, passing, and the next cycle re-runs it and finishes the sweep.
        fake.Gates["group-12"] = [new(9012, "completed", "success", Now.AddMinutes(-20))];
        await new Planner(fake, () => Now).SweepQueue(Locked, null, ct);
        Assert.Equal(["rerun:9012", "update:1"], fake.Order);
        Assert.Equal(Now.AddMinutes(-2), Markers.Time(fake.Find("owner/repo", 1).Body, QueueSweep.Swept));
    }

    // TS-S17 (b): a lapsed lease renewed and then a crash, with an older queue_swept left on the issue. The obligation is in
    // the issue, so the next cycle sweeps the group that passed its gate during the lapse.
    [Fact]
    public async Task ACrashRightAfterALeaseRenewalStillOwesTheSweep()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { StopAfterWrites = 1, Rerunning = Now };
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot",
            Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddMinutes(-90))),
                (QueueSweep.Required, Markers.Stamp(Now.AddHours(-5))), (QueueSweep.Swept, Markers.Stamp(Now.AddHours(-5)))));
        var watcher = new FakeGitHub();

        // The renewal writes the new generation and the cycle dies before it can report the lapse, let alone sweep.
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new Planner(fake, () => Now, alerts: new Alerts(watcher, "owner/watcher")).Renew(Locked, ct));
        fake.StopAfterWrites = null;
        Assert.Equal(Now, QueueSweep.Owed(fake.Find("owner/repo", 1).Body));

        // A group passed its gate during the lapse and is still queued, waiting for another required check.
        fake.Queued.Add(Group(12));
        fake.Gates["group-12"] = [new(9012, "completed", "success", Now.AddMinutes(-60))];
        await new Planner(fake, () => Now.AddMinutes(1)).SweepQueue(Locked, null, ct);

        Assert.Contains("rerun:9012", fake.Order);
        Assert.Equal(Now, Markers.Time(fake.Find("owner/repo", 1).Body, QueueSweep.Swept));
    }

    // A re-run GitHub refuses leaves the group unchecked, so the sweep is still owed and the worker keeps asking (ADR-016).
    [Fact]
    public async Task ARerunGitHubRefusesLeavesTheSweepOwed()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { RerunRefusal = "HTTP 403" };
        SeedSweep(fake, 2);
        fake.Queued.Add(Group(12));
        fake.Gates["group-12"] = [new(9012, "completed", "success", Now.AddMinutes(-20))];

        var lines = await new Planner(fake, () => Now).SweepQueue(Locked, null, ct);

        Assert.Equal(["rerun:9012"], fake.Order);
        Assert.Null(Markers.Time(fake.Find("owner/repo", 1).Body, QueueSweep.Swept));
        Assert.Contains("could not be re-run (HTTP 403)", lines.Single());
    }

    // The sandbox found GitHub's issue list not yet holding a lock created a second earlier, so the sweep the lock most needs
    // — the one for the groups already queued when it opened — found nothing to do. The Reporter hands over what it created.
    [Fact]
    public async Task ALockCreatedInThisCycleIsSweptWithoutWaitingForTheIssueList()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { JobConclusion = "failure", Rerunning = Now };
        fake.Queued.Add(Group(12));
        fake.Gates["group-12"] = [new(9012, "completed", "success", Now.AddMinutes(-20))];
        var reporter = new Reporter(fake, clock: () => Now);
        await reporter.Report(Locked, Pending(), ct);
        var lock1 = fake.Find("owner/repo", 1);
        Assert.Equal(Now, QueueSweep.Owed(lock1.Body));

        // The list has not caught up: without the hand-over there is nothing to sweep, and with it the gate is re-run at once.
        fake.Issues["owner/repo"].Clear();
        Assert.Empty(await new Planner(fake, () => Now).SweepQueue(Locked, null, ct));
        Assert.DoesNotContain("rerun:9012", fake.Order);

        fake.Issues["owner/repo"].Add(lock1);
        var lines = await new Planner(fake, () => Now).SweepQueue(Locked, [lock1.Issue], ct);
        Assert.Contains("rerun:9012", fake.Order);
        Assert.Contains("re-ran 1 gate run(s) — #12 (gate run 9012)", lines.Single());
        // The copy the list does hold wins, being at least as fresh: one sweep, one write, whichever way the lock arrived.
        Assert.Single(fake.Order, o => o == "update:1");
    }

    // Only an open App lock owes a sweep: a closed one enforces nothing, and a hand-made issue is not Main Watcher's lock.
    [Fact]
    public async Task NeitherAClosedLockNorAHandMadeOneIsSwept()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub();
        var closed = SeedSweep(fake, 2);
        closed.Issue = closed.Issue with { State = "closed", StateReason = "completed" };
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken: opened by hand", "alice", "User",
            Markers.Set("Locked.", (QueueSweep.Required, Markers.Stamp(Now))));
        fake.Queued.Add(Group(12));
        fake.Gates["group-12"] = [new(9012, "completed", "success", Now.AddMinutes(-20))];

        Assert.Empty(await new Planner(fake, () => Now).SweepQueue(Locked, null, ct));
        Assert.Empty(fake.Order);
        Assert.Empty(fake.Reads);
    }

    static readonly Target Locked = new() { Repo = "owner/repo" };
    /// <summary>The merge commit a pull request left on main, named the way GitHub names one.</summary>
    static MergedCommit Pull(int number, int minutesAgo) =>
        new(Sha('m'), $"Merge pull request #{number} from owner/branch", Now.AddMinutes(-minutesAgo), number);
    static PullEvent Labelled(bool added, int minutesAgo) =>
        new(added ? "labeled" : "unlabeled", Reconciliation.FixLabel, Now.AddMinutes(-minutesAgo));
    /// <summary>The merge in a pull request's own timeline, which is when ADR-015 judges its labels.</summary>
    static PullEvent Merge(int minutesAgo) => new(Reconciliation.Merged, null, Now.AddMinutes(-minutesAgo));
    static Planner Reconciler(FakeGitHub fake, FakeGitHub watcher, Func<DateTimeOffset>? clock = null) =>
        new(fake, clock ?? (() => Now), alerts: new Alerts(watcher, "owner/watcher"));

    /// <summary>A lock opened <paramref name="openedMinutesAgo"/> ago, with the body a real one carries.</summary>
    static FakeIssue SeedWindow(FakeGitHub fake, int openedMinutesAgo) =>
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot", LockBody(1, 'a'),
            Now.AddMinutes(-openedMinutesAgo));

    // TS-U4 (ADR-008): every unlabelled merge during an open lock is reported, each exactly once across runs.
    [Fact]
    public async Task EachUnlabelledMergeDuringALockIsReportedExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub
        {
            Activity =
            {
                PushAt('c', 'd', "push", "carol", 10),
                PushAt('b', 'c', "merge_queue_merge", "bob", 20),
                PushAt('a', 'b', "pr_merge", "alice", 30),
                // Before the lock existed: outside its window.
                PushAt('z', 'a', "pr_merge", "dave", 90),
            }
        };
        fake.Merged[Sha('b')] = [Pull(7, 30)];
        fake.Merged[Sha('c')] = [Pull(8, 20)];
        fake.Merged[Sha('a')] = [Pull(6, 90)];
        // #7 carried the label when it merged; #8 never did.
        fake.Labels[7] = [Labelled(true, 40), Merge(30)];
        fake.Labels[8] = [Merge(20)];
        SeedWindow(fake, 60);
        var watcher = new FakeGitHub();

        var lines = await Reconciler(fake, watcher).Reconcile(Locked, ct);
        // The open lock is amended in place; only a closed one is commented on.
        Assert.Equal(["update:1"], fake.Order);
        var body = fake.Find("owner/repo", 1).Body;
        Assert.Contains(Reconciliation.Heading, body);
        Assert.Contains("[#8](https://github.com/owner/repo/pull/8)", body);
        Assert.DoesNotContain("#7", body);
        Assert.DoesNotContain("#6", body);
        Assert.Equal(Now.AddMinutes(-20), Markers.Time(body, Reconciliation.Cursor));
        Assert.Null(Markers.Field(body, Reconciliation.Complete));
        Assert.Contains("reconciled 2 merge(s)", lines.Single());
        var alert = Assert.Single(watcher.Issues["owner/watcher"]).Issue;
        Assert.Equal("Merged while locked on owner/repo", alert.Title);
        Assert.Contains(Reconciliation.Key(8), alert.Body);

        // A second run finds nothing left: the cursor has passed both merges, and nothing is written or alerted again.
        Assert.Equal(["Lock #1: reconciled 0 merge(s) up to " + Markers.Stamp(Now) + "."],
            await Reconciler(fake, watcher).Reconcile(Locked, ct));
        Assert.Equal(["update:1"], fake.Order);
        Assert.Equal(["create:owner/watcher"], watcher.Order);
    }

    // TS-U10 (ADR-015): a closed lock is reconciled up to its closure, however it closed, and then marked complete.
    [Theory]
    [InlineData("main-watcher[bot]")]
    [InlineData("alice")]
    public async Task AClosedLockIsReconciledUpToItsClosureAndThenMarkedComplete(string closer)
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub
        {
            Activity = { PushAt('b', 'c', "pr_merge", "bob", 20), PushAt('a', 'b', "merge_queue_merge", "alice", 30) }
        };
        fake.Merged[Sha('b')] = [Pull(7, 30)];
        fake.Merged[Sha('c')] = [Pull(8, 20)];
        // Labelled after it had merged, which must not hide the report (ADR-015 point 8, TS-S15 (b)).
        fake.Labels[7] = [Merge(30), Labelled(true, 5)];
        SeedWindow(fake, 60);
        fake.CloseByHand("owner/repo", 1, closer, Now.AddMinutes(-25));
        var watcher = new FakeGitHub();

        var lines = await Reconciler(fake, watcher).Reconcile(Locked, ct);
        // The comment notifies the closed issue's participants; the row and the markers follow in one write.
        Assert.Equal(["comment:1", "update:1"], fake.Order);
        Assert.Contains("#7", fake.Comments.Single());
        var body = fake.Find("owner/repo", 1).Body;
        Assert.Contains("[#7](https://github.com/owner/repo/pull/7)", body);
        // The merge five minutes after the lock closed is outside its window.
        Assert.DoesNotContain("#8", body);
        Assert.Equal(Now.AddMinutes(-30), Markers.Time(body, Reconciliation.Cursor));
        Assert.Equal(Reconciliation.CompleteValue, Markers.Field(body, Reconciliation.Complete));
        Assert.Contains("its window is complete", lines.Single());
        Assert.Contains("Merged while locked on owner/repo", watcher.Issues["owner/watcher"].Select(i => i.Issue.Title));

        // A complete lock is never looked at again: no activity read, no write, no alert.
        fake.Reads.Clear();
        Assert.Empty(await Reconciler(fake, watcher).Reconcile(Locked, ct));
        Assert.Equal(["comment:1", "update:1"], fake.Order);
        Assert.Empty(fake.Reads);
    }

    // TS-U10: the label is judged as it was at merge time, not as it reads now.
    [Theory]
    [InlineData("", false)]
    [InlineData("+:-600", true)]
    // Added after the merge: the pull request still merged unlabelled, so it is still reported.
    [InlineData("+:600", false)]
    // Removed after the merge: it was a fix when it merged, so no false report.
    [InlineData("+:-600,-:600", true)]
    [InlineData("+:-600,-:-5", false)]
    // An event in the same second as the merge counts as before it.
    [InlineData("+:0", true)]
    [InlineData("-:0", false)]
    public void TheFixLabelIsJudgedFromTheEventsUpToTheMerge(string events, bool fix)
    {
        var history = events.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => new PullEvent(e[0] == '+' ? "labeled" : "unlabeled", Reconciliation.FixLabel,
                Now.AddSeconds(int.Parse(e[2..], System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        Assert.Equal(fix, Reconciliation.WasFix(history, Now));
        // Another label's history says nothing about this one.
        Assert.False(Reconciliation.WasFix(history.Select(e => e with { Label = "other" }), Now));
    }

    // The merge and squash subjects GitHub writes, which the gate matches too. A commit naming none belongs to a pull
    // request, but does not name it.
    [Theory]
    [InlineData("Merge pull request #12 from owner/fix", 12)]
    [InlineData("Add a feature (#8)", 8)]
    [InlineData("Add a feature (#8) and more", null)]
    [InlineData("Merge pull request #12", null)]
    [InlineData("a commit of its own", null)]
    [InlineData("#12", null)]
    public void ACommitSubjectNamesItsPullRequestOrNothing(string subject, int? pull) =>
        Assert.Equal(pull, Reconciliation.PullOf(subject));

    // ADR-015 judges the label at `merged_at`, which the merge queue builds a commit well before: a label added while the
    // entry waited in the queue was on the pull request when it merged.
    [Theory]
    // The timeline says the group merged 20 minutes ago, after the label went on: not reported.
    [InlineData(true, false)]
    // No merge in the timeline, so the commit's own date stands in, and by then the label was not on yet.
    [InlineData(false, true)]
    public async Task TheLabelIsJudgedAtTheTimelinesMergeNotTheCommitDate(bool timelineMerge, bool reported)
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { Activity = { PushAt('a', 'b', "merge_queue_merge", "alice", 20) } };
        // The merge commit was built 30 minutes ago; the group merged 20 minutes ago, and the label went on between.
        fake.Merged[Sha('b')] = [Pull(7, 30)];
        fake.Labels[7] = timelineMerge ? [Labelled(true, 25), Merge(20)] : [Labelled(true, 25)];
        SeedWindow(fake, 60);
        var watcher = new FakeGitHub();

        await Reconciler(fake, watcher).Reconcile(Locked, ct);
        Assert.Equal(reported, fake.Find("owner/repo", 1).Body.Contains("#7", StringComparison.Ordinal));
        Assert.Equal(reported, watcher.Issues.Count == 1);
    }

    // NFR-4 is about what landed, not about what could be named: a merge whose pull request cannot be identified is reported
    // rather than passed over, since nothing shows it carried the label.
    [Theory]
    [InlineData(false, "None of its commit subjects names a pull request")]
    [InlineData(true, "Its range can no longer be compared")]
    public async Task AMergeWhosePullRequestCannotBeNamedIsStillReported(bool uncomparable, string says)
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { Activity = { PushAt('a', 'b', "merge_queue_merge", "alice", 30) } };
        if (uncomparable) fake.Uncomparable.Add(Sha('b'));
        else fake.Merged[Sha('b')] = [new(Sha('c'), "a commit of its own", Now.AddMinutes(-30), null)];
        SeedWindow(fake, 60);
        var watcher = new FakeGitHub();

        await Reconciler(fake, watcher).Reconcile(Locked, ct);
        var body = fake.Find("owner/repo", 1).Body;
        Assert.Contains("A merge-queue merge left", body);
        Assert.Contains(says, body);
        Assert.Contains(Reconciliation.Key(Sha('b')), body);
        // The label history is never asked for: there is no pull request to ask about.
        Assert.DoesNotContain(fake.Reads, r => r.StartsWith("labels:"));
        Assert.Single(watcher.Issues["owner/watcher"]);
        // The cursor still moves past it, so the same merge is not reported again.
        Assert.Equal(Now.AddMinutes(-30), Markers.Time(body, Reconciliation.Cursor));
        await Reconciler(fake, watcher).Reconcile(Locked, ct);
        Assert.Equal(["update:1"], fake.Order);
    }

    // TS-U10: a label history that cannot be read stops the pass there; the cursor stays behind the merge it could not judge.
    [Fact]
    public async Task AnUnreadableLabelHistoryStopsWithoutAdvancingTheCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub
        {
            Activity = { PushAt('b', 'c', "pr_merge", "bob", 20), PushAt('a', 'b', "pr_merge", "alice", 30) }
        };
        fake.Merged[Sha('b')] = [Pull(7, 30)];
        fake.Merged[Sha('c')] = [Pull(8, 20)];
        fake.LabelsError.Add(8);
        SeedWindow(fake, 60);
        fake.CloseByHand("owner/repo", 1, "alice", Now.AddMinutes(-5));
        var watcher = new FakeGitHub();

        var lines = await Reconciler(fake, watcher).Reconcile(Locked, ct);
        var body = fake.Find("owner/repo", 1).Body;
        Assert.Equal(Now.AddMinutes(-30), Markers.Time(body, Reconciliation.Cursor));
        // Never complete while a merge inside the window is unjudged, so the next run looks again.
        Assert.Null(Markers.Field(body, Reconciliation.Complete));
        Assert.Contains("the label history of #8 could not be read", lines.Single());

        fake.LabelsError.Clear();
        await Reconciler(fake, watcher).Reconcile(Locked, ct);
        body = fake.Find("owner/repo", 1).Body;
        Assert.Equal(Now.AddMinutes(-20), Markers.Time(body, Reconciliation.Cursor));
        Assert.Equal(Reconciliation.CompleteValue, Markers.Field(body, Reconciliation.Complete));
        Assert.Contains("#8", body);
        // Each merge reported once: #7 on the first pass, #8 on the second.
        Assert.Equal(2, watcher.Comments.Count + watcher.Issues["owner/watcher"].Count);
    }

    // TS-U10: complete is written only after every report succeeded, and a replay repeats none of them.
    [Fact]
    public async Task CompleteIsWrittenOnlyAfterEveryReportSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { Activity = { PushAt('a', 'b', "pr_merge", "alice", 30) } };
        fake.Merged[Sha('b')] = [Pull(7, 30)];
        SeedWindow(fake, 60);
        fake.CloseByHand("owner/repo", 1, "alice", Now.AddMinutes(-5));
        var watcher = new FakeGitHub();

        // Stopped, as a crash would, immediately after the comment: nothing says the window is complete.
        fake.StopAfterWrites = 1;
        await Assert.ThrowsAsync<HttpRequestException>(() => Reconciler(fake, watcher).Reconcile(Locked, ct));
        Assert.Null(Markers.Field(fake.Find("owner/repo", 1).Body, Reconciliation.Complete));

        fake.StopAfterWrites = null;
        await Reconciler(fake, watcher).Reconcile(Locked, ct);
        // One comment, one alert and one row, although the report ran twice.
        Assert.Single(fake.Find("owner/repo", 1).Comments);
        Assert.Single(watcher.Issues["owner/watcher"]);
        var body = fake.Find("owner/repo", 1).Body;
        Assert.Equal(1, body.Split(Reconciliation.Key(7)).Length - 1);
        Assert.Equal(Reconciliation.CompleteValue, Markers.Field(body, Reconciliation.Complete));
    }

    // PR #55 review: the cursor is read back with a strict ">", so a second's worth of activity moves it all at once.
    // Advancing between two entries stamped in the same second would put the one still unjudged behind it for good, and the
    // next run would then find nothing left and mark the lock complete.
    [Fact]
    public async Task TwoMergesInTheSameSecondMoveTheCursorOnlyTogether()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub
        {
            Activity = { PushAt('a', 'b', "pr_merge", "alice", 30), PushAt('b', 'c', "pr_merge", "bob", 30) }
        };
        fake.Merged[Sha('b')] = [Pull(7, 30)];
        fake.Merged[Sha('c')] = [Pull(8, 30)];
        fake.Labels[7] = [Merge(30)];
        fake.LabelsError.Add(8);
        SeedWindow(fake, 60);
        fake.CloseByHand("owner/repo", 1, "alice", Now.AddMinutes(-5));
        var watcher = new FakeGitHub();

        await Reconciler(fake, watcher).Reconcile(Locked, ct);
        var body = fake.Find("owner/repo", 1).Body;
        Assert.Contains("#7", body);
        // Neither the cursor nor the completion moves while #8, stamped in the same second, is unjudged.
        Assert.Null(Markers.Field(body, Reconciliation.Cursor));
        Assert.Null(Markers.Field(body, Reconciliation.Complete));

        fake.LabelsError.Clear();
        await Reconciler(fake, watcher).Reconcile(Locked, ct);
        body = fake.Find("owner/repo", 1).Body;
        Assert.Contains("#8", body);
        Assert.Equal(Now.AddMinutes(-30), Markers.Time(body, Reconciliation.Cursor));
        Assert.Equal(Reconciliation.CompleteValue, Markers.Field(body, Reconciliation.Complete));
        // #7 was looked at twice and reported once.
        Assert.Equal(1, body.Split(Reconciliation.Key(7)).Length - 1);
        Assert.Single(fake.Find("owner/repo", 1).Comments, c => c.Body.Contains(Reconciliation.Key(7), StringComparison.Ordinal));
    }

    // PR #55 review: a pull request is named by its **last** commit, so a range read only part-way loses exactly the commits
    // that name the later ones. A lock is never complete while any of its range is unread — the bound is on the work one
    // cycle does, not on what is checked — and the next pass carries on from where this one stopped.
    [Fact]
    public async Task ALongRangeIsFinishedAcrossPassesAndTheLockStaysIncompleteMeanwhile()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { Activity = { PushAt('a', 'b', "merge_queue_merge", "alice", 30) } };
        // Two pull requests in one range; the fake yields one commit a pass while the range is marked longer than a pass.
        fake.Merged[Sha('b')] = [Pull(7, 30), Pull(8, 30)];
        fake.Truncated.Add(Sha('b'));
        fake.Labels[7] = [Merge(30)];
        fake.Labels[8] = [Merge(30)];
        SeedWindow(fake, 60);
        fake.CloseByHand("owner/repo", 1, "alice", Now.AddMinutes(-5));
        var watcher = new FakeGitHub();

        var line = Assert.Single(await Reconciler(fake, watcher).Reconcile(Locked, ct));
        var body = fake.Find("owner/repo", 1).Body;
        // The first pass judged #7 and stopped. #8 is named only by a commit it has not read yet.
        Assert.Contains("[#7](https://github.com/owner/repo/pull/7)", body);
        Assert.DoesNotContain("#8", body);
        // Nothing it could not read is reported as unnameable, and nothing claims the window was checked.
        Assert.DoesNotContain(Reconciliation.Key(Sha('b')), body);
        Assert.Null(Markers.Field(body, Reconciliation.Cursor));
        Assert.Null(Markers.Field(body, Reconciliation.Complete));
        Assert.Equal($"{Sha('b')}:1", Markers.Field(body, Reconciliation.Commits));
        Assert.Contains("is longer than one pass reads", line);

        // The next pass carries on from the commit the last one stopped at, and finishes the entry.
        await Reconciler(fake, watcher).Reconcile(Locked, ct);
        body = fake.Find("owner/repo", 1).Body;
        Assert.Contains("[#8](https://github.com/owner/repo/pull/8)", body);
        Assert.Equal(Now.AddMinutes(-30), Markers.Time(body, Reconciliation.Cursor));
        Assert.Equal(Reconciliation.CompleteValue, Markers.Field(body, Reconciliation.Complete));
        Assert.Equal(["merged:" + Sha('b') + ":0", "labels:7", "merged:" + Sha('b') + ":1", "labels:8"], fake.Reads);
        // Each pull request reported once, and each judged on its own rather than lumped into one warning.
        Assert.Equal(2, watcher.Comments.Count + watcher.Issues["owner/watcher"].Count);
    }

    // PR #55 review: the two rules above meet here. The cursor cannot move until a whole second is done, so while one entry
    // of a second is unfinished its neighbours cannot be put behind it — and an entry whose progress was forgotten would be
    // read again from the start. Two long ranges in one second would then take turns overwriting each other's progress and
    // neither would ever finish.
    [Fact]
    public async Task TwoLongRangesInTheSameSecondBothFinish()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub
        {
            Activity = { PushAt('a', 'b', "merge_queue_merge", "alice", 30), PushAt('b', 'c', "merge_queue_merge", "bob", 30) }
        };
        // Two entries stamped in the same second, each naming two pull requests and each read one commit a pass.
        fake.Merged[Sha('b')] = [Pull(7, 30), Pull(8, 30)];
        fake.Merged[Sha('c')] = [Pull(9, 30), Pull(10, 30)];
        fake.Truncated.Add(Sha('b'));
        fake.Truncated.Add(Sha('c'));
        foreach (var pull in new[] { 7, 8, 9, 10 }) fake.Labels[pull] = [Merge(30)];
        SeedWindow(fake, 60);
        fake.CloseByHand("owner/repo", 1, "alice", Now.AddMinutes(-5));
        var watcher = new FakeGitHub();

        // Three passes finish both ranges: b, then b's tail and c, then c's tail.
        for (var pass = 0; pass < 3; pass++) await Reconciler(fake, watcher).Reconcile(Locked, ct);
        var body = fake.Find("owner/repo", 1).Body;
        foreach (var pull in new[] { 7, 8, 9, 10 })
            Assert.Contains($"[#{pull}](https://github.com/owner/repo/pull/{pull})", body);
        Assert.Equal(Now.AddMinutes(-30), Markers.Time(body, Reconciliation.Cursor));
        Assert.Equal(Reconciliation.CompleteValue, Markers.Field(body, Reconciliation.Complete));
        // Nothing of that second needs remembering once it is behind the cursor.
        Assert.Equal("", Markers.Field(body, Reconciliation.Commits));
        // The first range is never restarted: each commit of each range is read exactly once.
        Assert.Equal([
            "merged:" + Sha('b') + ":0", "labels:7",
            "merged:" + Sha('b') + ":1", "labels:8", "merged:" + Sha('c') + ":0", "labels:9",
            "merged:" + Sha('c') + ":1", "labels:10"], fake.Reads);
    }

    // PR #55 review: the writes that fail are the ones that would have said something, so nothing else would notice. The
    // overdue alert goes to the watcher repo, which is writable when the target's issue is not.
    [Fact]
    public async Task AClosedLockWhoseReportsCannotBeWrittenStillRaisesReconciliationFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { Activity = { PushAt('a', 'b', "pr_merge", "alice", 1500) }, IssueError = true };
        fake.Merged[Sha('b')] = [Pull(7, 1500)];
        fake.Labels[7] = [Merge(1500)];
        SeedWindow(fake, 1600);
        fake.CloseByHand("owner/repo", 1, "alice", Now.AddMinutes(-1490));
        var watcher = new FakeGitHub();

        // The cycle still fails, so the worker asks again.
        await Assert.ThrowsAsync<HttpRequestException>(() => Reconciler(fake, watcher).Reconcile(Locked, ct));
        var alert = Assert.Single(watcher.Issues["owner/watcher"]).Issue;
        Assert.Equal("Reconciliation failing on owner/repo", alert.Title);
        Assert.Contains("has still not been checked", alert.Body);
        Assert.Contains("its reports could not be written", alert.Body);
        // Nothing was written, so nothing advanced and nothing claims the window was checked.
        var body = fake.Find("owner/repo", 1).Body;
        Assert.Null(Markers.Field(body, Reconciliation.Cursor));
        Assert.Null(Markers.Field(body, Reconciliation.Complete));
    }

    [Fact]
    public async Task AMergeMadeWhileLockedCannotBeReportedWithoutAnAlertSink()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { Activity = { PushAt('a', 'b', "pr_merge", "alice", 30) } };
        fake.Merged[Sha('b')] = [Pull(7, 30)];
        SeedWindow(fake, 60);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Planner(fake, () => Now).Reconcile(Locked, ct));
        Assert.Empty(fake.Order);
    }

    // ADR-015 point 7: reconciliation that does not finish becomes visible, whichever clause is the one that is overdue.
    [Theory]
    // The merge itself has been unjudged for more than a day, although the lock closed only an hour ago.
    [InlineData(60, 1500, true, "is still unjudged")]
    // The lock closed more than a day ago and its window has still not been checked.
    [InlineData(1500, 1530, true, "has still not been checked")]
    [InlineData(60, 90, false, null)]
    public async Task ReconciliationThatKeepsFailingIsAlerted(int closedMinutesAgo, int mergedMinutesAgo, bool alerted, string? says)
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { Activity = { PushAt('a', 'b', "pr_merge", "alice", mergedMinutesAgo) } };
        fake.MergedError.Add(Sha('b'));
        SeedWindow(fake, Math.Max(closedMinutesAgo, mergedMinutesAgo) + 30);
        fake.CloseByHand("owner/repo", 1, "alice", Now.AddMinutes(-closedMinutesAgo));
        var watcher = new FakeGitHub();

        await Reconciler(fake, watcher).Reconcile(Locked, ct);
        var alerts = watcher.Issues.GetValueOrDefault("owner/watcher", []);
        Assert.Equal(alerted, alerts.Count == 1);
        if (says is not null) Assert.Contains(says, alerts.Single().Body);
        if (!alerted) return;
        // One comment a day, not one a cycle: the key carries the date.
        await Reconciler(fake, watcher).Reconcile(Locked, ct);
        Assert.Equal(["create:owner/watcher"], watcher.Order);
        await Reconciler(fake, watcher, () => Now.AddDays(1)).Reconcile(Locked, ct);
        Assert.Equal(["create:owner/watcher", "comment:1"], watcher.Order);
    }

    // R-7 and §17: unlocking forgets the pull requests the gate removed unless the comment names them.
    [Fact]
    public async Task TheUnlockCommentListsThePullRequestsTheGateRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { JobConclusion = "success", ReportResult = new(true, []) };
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot", "", Now.AddHours(-2));
        fake.Blocked.Add(new(9001, 12, $"gh-readonly-queue/main/pr-12-{Sha('a')}", Now.AddHours(-1)));
        // The same pull request removed twice is named once; one removed before the lock opened is not this lock's doing.
        fake.Blocked.Add(new(9002, 12, $"gh-readonly-queue/main/pr-12-{Sha('b')}", Now.AddMinutes(-30)));
        fake.Blocked.Add(new(9003, 5, $"gh-readonly-queue/main/pr-5-{Sha('c')}", Now.AddHours(-3)));
        // ADR-016: #20 was queued before this lock and was removed by the sweep re-running its gate, which is a second attempt
        // of a run GitHub still dates by its first. The comment must name it too, and say why it went.
        fake.Blocked.Add(new(9004, 20, $"gh-readonly-queue/main/pr-20-{Sha('d')}", Now.AddMinutes(-90), Attempt: 2));

        await new Reporter(fake, clock: () => Now).Report(Locked, Pending(), ct);
        var comment = fake.Comments.Single();
        Assert.Equal(new[] { "comment:1", "close:1", "complete:success" }, fake.Order);
        Assert.Contains("re-queue the ones you still want merged", comment);
        Assert.Equal(1, comment.Split("- #12").Length - 1);
        Assert.DoesNotContain("- #5", comment);
        Assert.Contains("- #20 (its gate was re-run because it was queued before this lock)", comment);
        Assert.DoesNotContain("- #12 (its gate", comment);
    }

    [Fact]
    public async Task AnUnreadableGateRunListStillClosesTheLock()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { JobConclusion = "success", ReportResult = new(true, []), BlockedError = true };
        fake.Seed("owner/repo", Reporter.LockLabel, "main is broken", "main-watcher[bot]", "Bot", "", Now.AddHours(-2));
        await new Reporter(fake, clock: () => Now).Report(Locked, Pending(), ct);
        Assert.Contains("could not be read", fake.Comments.Single());
        Assert.Equal(new[] { "comment:1", "close:1", "complete:success" }, fake.Order);
    }

    // TS-U11. Steps are "name=conclusion" in job order; "-" is a step with no conclusion.
    [Theory]
    [InlineData(OutcomeKind.Passed, "Restore=success", "main-watcher-test=success", "main-watcher-tests-finished=success")]
    [InlineData(OutcomeKind.Failed, "main-watcher-test=failure", "main-watcher-tests-finished=success")]
    // A finished test step wins over anything later in the run: a hung upload, a timeout or a cancellation.
    [InlineData(OutcomeKind.Failed, "main-watcher-test=failure", "main-watcher-tests-finished=success", "Upload CTRF reports=failure", "Summary=cancelled")]
    [InlineData(OutcomeKind.Passed, "main-watcher-test=success", "main-watcher-tests-finished=success", "Upload CTRF reports=cancelled")]
    // The tests did not finish. A step cut off by its own timeout-minutes is reported as failure.
    [InlineData(OutcomeKind.InfrastructureError, "Restore=failure", "main-watcher-test=skipped", "main-watcher-tests-finished=skipped")]
    [InlineData(OutcomeKind.InfrastructureError, "main-watcher-test=failure", "main-watcher-tests-finished=skipped")]
    [InlineData(OutcomeKind.InfrastructureError, "main-watcher-test=cancelled", "main-watcher-tests-finished=cancelled")]
    [InlineData(OutcomeKind.InfrastructureError, "main-watcher-test=failure")]
    [InlineData(OutcomeKind.InfrastructureError, "main-watcher-test=-", "main-watcher-tests-finished=-")]
    [InlineData(OutcomeKind.InfrastructureError)]
    // A renamed marker step is indistinguishable from tests that did not finish.
    [InlineData(OutcomeKind.InfrastructureError, "main-watcher-test=failure", "main-watcher-tests-done=success")]
    // The marker succeeded, but the test step is missing, renamed or in any other state.
    [InlineData(OutcomeKind.ContractBroken, "main-watcher-tests-finished=success")]
    [InlineData(OutcomeKind.ContractBroken, "main-watcher-tests=failure", "main-watcher-tests-finished=success")]
    [InlineData(OutcomeKind.ContractBroken, "main-watcher-test=cancelled", "main-watcher-tests-finished=success")]
    [InlineData(OutcomeKind.ContractBroken, "main-watcher-test=skipped", "main-watcher-tests-finished=success")]
    // Either step found twice, even when the other rows would decide.
    [InlineData(OutcomeKind.ContractBroken, "main-watcher-test=success", "main-watcher-test=failure", "main-watcher-tests-finished=success")]
    [InlineData(OutcomeKind.ContractBroken, "main-watcher-test=failure", "main-watcher-tests-finished=success", "main-watcher-tests-finished=success")]
    [InlineData(OutcomeKind.ContractBroken, "main-watcher-test=failure", "main-watcher-tests-finished=skipped", "main-watcher-tests-finished=skipped")]
    public void OutcomeTable(OutcomeKind expected, params string[] steps)
    {
        var job = new WorkflowJob("caller / main-watcher", "completed", steps.Select(s => s.Split('=')).Select(p => new JobStep(p[0], p[1] == "-" ? null : p[1])).ToArray());
        var outcome = Outcomes.Read([new("caller / report", "queued", []), job])!;
        Assert.Equal(expected, outcome.Kind);
        Assert.Equal(expected switch { OutcomeKind.Passed => "success", OutcomeKind.Failed => "failure", _ => "neutral" }, outcome.Conclusion);
    }

    [Fact]
    public void OutcomeTableForRunsAndJobs()
    {
        Assert.Equal(OutcomeKind.Unknown, Outcomes.Read(null)!.Kind);
        Assert.Null(Outcomes.Read([new("caller / main-watcher", "in_progress", [new("main-watcher-test", "failure"), new("main-watcher-tests-finished", "success")])]));
        Assert.Equal(OutcomeKind.ContractBroken, Outcomes.Read([])!.Kind);
        Assert.Equal(OutcomeKind.ContractBroken, Outcomes.Read([new("a / main-watcher", "completed", []), new("b / main-watcher", "completed", [])])!.Kind);
        var infra = Outcomes.Read([new("caller / main-watcher", "completed", [new("Restore", "failure"), new("main-watcher-test", "skipped"), new("@team `x`", null)])])!;
        Assert.Contains("- Restore: failure\n- main-watcher-test: skipped\n- &#64;team \\`x\\`: no conclusion", infra.Description);
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
        // No targets is a valid configuration: the sweep and the worker then have nothing to do.
        Assert.Empty(TargetConfiguration.Parse("targets: []").Targets);
        // lock_lease (ADR-014) is one setting for the whole file, and the gate rejects a lease more than 24 hours ahead.
        Assert.Equal(Lease.Default, target.LockLease);
        Assert.Equal(TimeSpan.FromMinutes(10), TargetConfiguration.Parse("lock_lease: 10\ntargets:\n  - repo: owner/repo").Targets.Single().LockLease);
        Assert.Throws<InvalidDataException>(() => TargetConfiguration.Parse("lock_lease: 1441\ntargets: []"));
        Assert.Throws<InvalidDataException>(() => TargetConfiguration.Parse("lock_lease: 0\ntargets: []"));
        // It is set once, never per target: one repository cannot hold the queue longer than another.
        Assert.ThrowsAny<Exception>(() => TargetConfiguration.Parse("targets:\n  - repo: owner/repo\n    lock_lease: 10"));
    }

    /// <summary>
    /// The committed <c>targets.yml</c> parses. A file the sweep cannot read shows up only as a failing cycle an hour later,
    /// so it is checked here, with the parser the cycle and the trigger worker both use.
    /// </summary>
    [Fact]
    public void CommittedTargetListParses()
    {
        var targets = TargetConfiguration.Parse(Fixture("targets.yml")).Targets;
        // The watcher repo watches no sandbox target: the private replica does, and two watchers would race for its
        // check runs (sandbox/README.md). The clause is the watcher repo's own invariant, not its tree's, so it is
        // scoped by GITHUB_REPOSITORY: the replica runs this same test over a list that watches the sandbox target
        // on purpose, and asserting there would leave it red on its own working state (#53). Do not unscope it.
        // Away from Actions the variable is unset, which is a checkout of this repo, so the clause still applies.
        if (RunningInTheSandboxReplica)
        {
            return;
        }

        Assert.DoesNotContain(targets, t => t.Repo.StartsWith(SandboxOwner, StringComparison.OrdinalIgnoreCase));
    }

    const string SandboxOwner = "main-watcher-sandbox/";

    static bool RunningInTheSandboxReplica =>
        (Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "").StartsWith(SandboxOwner, StringComparison.OrdinalIgnoreCase);

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
        Assert.Equal(Now.Add(Lease.Default), MainWatcher.Gate.LockLease.ReadLeaseUntil(issue.Body));
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
        Assert.Equal(Now.Add(Lease.Default), MainWatcher.Gate.LockLease.ReadLeaseUntil(body));
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
        Assert.Equal(Now.Add(Lease.Default), MainWatcher.Gate.LockLease.ReadLeaseUntil(body));
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

    static readonly WorkflowJob RestoreFailed = new("tests / main-watcher", "completed",
        [new("Restore", "failure"), new("main-watcher-test", "skipped"), new("main-watcher-tests-finished", "skipped"), new("Upload CTRF reports", "success")]);

    // TS-S18: the sandbox switch's boundary. The neutral write is the last thing the report does, so a cycle stopped right
    // after it has done everything the retest needs; the rule alone brings the head back.
    [Fact]
    public async Task TheNeutralCheckRunWriteIsNamedForTheFaultSwitch()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { JobList = [RestoreFailed] };
        var writes = new List<string>();
        var reporter = new Reporter(fake, new Alerts(fake, "owner/watcher"), afterWrite: writes.Add);
        Assert.True(await reporter.Report(Watched, Pending(Sha('b')) with { Id = 7 }, ct));
        Assert.Equal(["check:neutral"], writes);
    }

    [Fact]
    public async Task InfrastructureErrorAlertsThenCompletesNeutralWithoutALock()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { JobList = [RestoreFailed] };
        SeedLock(fake, 1, 'a');
        var reporter = new Reporter(fake, new Alerts(fake, "owner/watcher"));
        Assert.True(await reporter.Report(Watched, Pending(Sha('b')) with { Id = 7 }, ct));
        Assert.Equal("neutral", fake.Conclusion);
        // The alert is written first, and the open lock is neither commented on nor closed.
        Assert.Equal(new[] { "create:owner/watcher", "complete:neutral" }, fake.Order);
        Assert.True(fake.Find("owner/repo", 1).Open);
        Assert.Single(fake.Issues["owner/repo"]);
        var alert = fake.Issues["owner/watcher"].Single();
        Assert.Equal(("Infrastructure error on owner/repo", "watcher-infra"), (alert.Issue.Title, alert.Label));
        Assert.Contains("Check run 7 on [`bbbbbbb`]", alert.Body);
        Assert.Contains("- Restore: failure\n- main-watcher-test: skipped\n- main-watcher-tests-finished: skipped", alert.Body);
        Assert.Contains("https://github.com/owner/repo/actions/runs/42", alert.Body);
        Assert.Contains("- Restore: failure", fake.Summary);
        Assert.Contains("No lock was opened", fake.Summary);
        Assert.Empty(reporter.AlertFailures);
    }

    [Fact]
    public async Task ReplayedNeutralReportDoesNotRepeatItsAlert()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { JobList = [RestoreFailed] };
        var alerts = new FakeGitHub();
        var check = Pending(Sha('b')) with { Id = 7 };
        await new Reporter(fake, new Alerts(alerts, "owner/watcher")).Report(Watched, check, ct);
        await new Reporter(fake, new Alerts(alerts, "owner/watcher")).Report(Watched, check, ct);
        Assert.Equal(new[] { "create:owner/watcher" }, alerts.Order);
        // A later check with the same error comments on the open alert, once.
        await new Reporter(fake, new Alerts(alerts, "owner/watcher")).Report(Watched, check with { Id = 8 }, ct);
        await new Reporter(fake, new Alerts(alerts, "owner/watcher")).Report(Watched, check with { Id = 8 }, ct);
        Assert.Equal(new[] { "create:owner/watcher", "comment:1" }, alerts.Order);
        Assert.EndsWith("<!-- main-watcher check=8 -->", alerts.Comments.Single());
    }

    [Theory]
    [InlineData(true, null, "Outcome unknown on owner/repo", "the target run was deleted")]
    [InlineData(false, "main-watcher-tests", "Outcome contract broken on owner/repo", "succeeded without a `main-watcher-test` result")]
    [InlineData(false, "main-watcher-test", "Outcome contract broken on owner/repo", "appears more than once")]
    public async Task DeletedRunsAndBrokenContractsAlertAndStayNeutral(bool deleted, string? testStep, string title, string description)
    {
        var steps = new List<JobStep> { new(testStep ?? "", "failure"), new("main-watcher-tests-finished", "success") };
        if (testStep == "main-watcher-test") steps.Add(new("main-watcher-test", "success"));
        var fake = new FakeGitHub { RunDeleted = deleted, JobList = [new("tests / main-watcher", "completed", steps)] };
        var alerts = new FakeGitHub();
        var reporter = new Reporter(fake, new Alerts(alerts, "owner/watcher"));
        Assert.True(await reporter.Report(Watched, Pending(Sha('b')), TestContext.Current.CancellationToken));
        Assert.Equal("neutral", fake.Conclusion);
        Assert.False(fake.Issues.ContainsKey("owner/repo"));
        var alert = alerts.Issues["owner/watcher"].Single();
        Assert.Equal(title, alert.Issue.Title);
        Assert.Contains(description, alert.Body, StringComparison.OrdinalIgnoreCase);
    }

    // The previous check run's conclusion and output title, as the Reporter or the Planner completed it.
    [Theory]
    [InlineData("neutral", "Infrastructure error", true)]
    [InlineData("neutral", "Outcome contract broken", false)]
    [InlineData("neutral", "Outcome unknown", false)]
    [InlineData("success", "Tests passed", false)]
    [InlineData("failure", "Tests failed", false)]
    public async Task TwoInfrastructureErrorsInARowRaiseAnAlert(string previous, string previousTitle, bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var check = Pending(Sha('b')) with { Id = 7 };
        var fake = new FakeGitHub
        {
            JobList = [RestoreFailed],
            CheckList =
            [
                new(5, Sha('a'), "completed", "neutral", check.StartedAt.AddHours(-2), check.StartedAt.AddHours(-2), "40", "Infrastructure error"),
                new(6, Sha('a'), "completed", previous, check.StartedAt.AddMinutes(-20), check.StartedAt.AddMinutes(-15), "41", previousTitle),
                check,
            ],
        };
        var alerts = new FakeGitHub();
        var reporter = new Reporter(fake, new Alerts(alerts, "owner/watcher"));
        await reporter.Report(Watched, check, ct);
        await reporter.Report(Watched, check, ct);
        var titles = alerts.Issues["owner/watcher"].Select(i => i.Issue.Title).ToArray();
        Assert.Equal(expected, titles.Contains("Infrastructure errors twice in a row on owner/repo"));
        Assert.Equal(expected ? 2 : 1, titles.Length);
        if (expected) Assert.Contains($"The previous check run, 6 on [`aaaaaaa`](https://github.com/owner/repo/commit/{Sha('a')}), was also an infrastructure error.", alerts.Issues["owner/watcher"][1].Body);
        Assert.Empty(alerts.Comments);
        Assert.Equal(("neutral", "Infrastructure error"), (fake.Conclusion, fake.Title));
    }

    [Fact]
    public async Task ContractErrorsDoNotStartOrContinueAnInfrastructureStreak()
    {
        var ct = TestContext.Current.CancellationToken;
        var check = Pending(Sha('b')) with { Id = 7 };
        var previous = new CheckRun(6, Sha('a'), "completed", "neutral", check.StartedAt.AddMinutes(-20), check.StartedAt.AddMinutes(-15), "41", "Infrastructure error");
        var fake = new FakeGitHub { RunDeleted = true, CheckList = [previous] };
        var alerts = new FakeGitHub();
        await new Reporter(fake, new Alerts(alerts, "owner/watcher")).Report(Watched, check, ct);
        Assert.Equal("Outcome unknown on owner/repo", alerts.Issues["owner/watcher"].Single().Issue.Title);
        Assert.Equal("Outcome unknown", fake.Title);
    }

    [Fact]
    public async Task NeutralOutputTitlesRecordTheKind()
    {
        var ct = TestContext.Current.CancellationToken;
        var renamed = new FakeGitHub { JobList = [new("tests / main-watcher", "completed", [new("main-watcher-tests-finished", "success")])] };
        await new Reporter(renamed, new Alerts(new FakeGitHub(), "owner/watcher")).Report(Watched, Pending(Sha('b')), ct);
        Assert.Equal(("neutral", "Outcome contract broken"), (renamed.Conclusion, renamed.Title));
        var red = new FakeGitHub();
        await new Reporter(red).Report(Watched, Pending(Sha('b')), ct);
        Assert.Equal(("failure", "Tests failed"), (red.Conclusion, red.Title));
    }

    [Fact]
    public async Task FailedNeutralAlertLeavesTheCheckInProgressAndTheReplayRaisesEachAlertOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var check = Pending(Sha('b')) with { Id = 7 };
        var previous = new CheckRun(6, Sha('a'), "completed", "neutral", check.StartedAt.AddMinutes(-20), check.StartedAt.AddMinutes(-15), "41", "Infrastructure error");

        // The alert sink is down: nothing is completed.
        var fake = new FakeGitHub { JobList = [RestoreFailed], CheckList = [previous] };
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake, new Alerts(new FakeGitHub { IssueError = true }, "owner/watcher")).Report(Watched, check, ct));
        Assert.Null(fake.Conclusion);

        // The first alert is raised, then the check runs cannot be read for the streak: still nothing is completed.
        var alerts = new FakeGitHub();
        fake.ChecksError = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => new Reporter(fake, new Alerts(alerts, "owner/watcher")).Report(Watched, check, ct));
        Assert.Null(fake.Conclusion);
        Assert.Equal(new[] { "create:owner/watcher" }, alerts.Order);

        // The replay skips the alert already raised, raises the streak alert, and completes the check.
        fake.ChecksError = false;
        Assert.True(await new Reporter(fake, new Alerts(alerts, "owner/watcher")).Report(Watched, check, ct));
        Assert.Equal(new[] { "create:owner/watcher", "create:owner/watcher" }, alerts.Order);
        Assert.Equal("Infrastructure errors twice in a row on owner/repo", alerts.Issues["owner/watcher"][1].Issue.Title);
        Assert.Equal("neutral", fake.Conclusion);
    }

    [Fact]
    public async Task NeutralResultWithoutAnAlertSinkIsNotCompleted()
    {
        var fake = new FakeGitHub { JobList = [RestoreFailed] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Reporter(fake).Report(Watched, Pending(Sha('b')), TestContext.Current.CancellationToken));
        Assert.Null(fake.Conclusion);
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
        Assert.Equal(Now.Add(Lease.Default), MainWatcher.Gate.LockLease.ReadLeaseUntil(body));
        // ADR-016: the sweep obligation is created by the same write as the lock, so a crash straight after cannot lose it.
        Assert.Equal(Now, QueueSweep.Owed(body));
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

    [Fact]
    public async Task AnAlertJustOpenedTakesTheNextOneAlthoughTheListLags()
    {
        // The issue list does not hold an issue created a second earlier: two merges reported 2 s apart opened two alerts (#25).
        var fake = new FakeGitHub { ListLags = true };
        var at = Now;
        var alerts = new Alerts(fake, "owner/watcher", () => at);
        var ct = TestContext.Current.CancellationToken;
        await alerts.Raise("Merged while locked on owner/repo", "#6 merged", ct, "<!-- pr=6 -->");
        await alerts.Raise("Merged while locked on owner/repo", "#7 merged", ct, "<!-- pr=7 -->");
        await alerts.Raise("Merged while locked on owner/repo", "#6 merged", ct, "<!-- pr=6 -->");
        var alert = Assert.Single(fake.Issues["owner/watcher"]);
        Assert.Contains("<!-- pr=6 -->", alert.Issue.Body);
        Assert.Equal(new[] { "#7 merged\n\n<!-- pr=7 -->" }, fake.Comments);

        // Trusted only briefly: past the window, an alert the list still does not show, as when a person has closed it, is not used.
        at += Alerts.Remembered + TimeSpan.FromSeconds(1);
        await alerts.Raise("Merged while locked on owner/repo", "#8 merged", ct, "<!-- pr=8 -->");
        Assert.Equal(2, fake.Issues["owner/watcher"].Count);
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
        if (stopAfter is null) Assert.Equal(new[] { "comment", "close", "comment", "update", "check:failure" }, writes);
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

    // A lock closed by hand between a cycle's override check and its reconciliation was marked complete, so the worker saw no
    // work and the override comment waited for an unrelated cycle (TS-S14 in the scenario suite, #25).
    [Fact]
    public async Task ALockClosedDuringACycleIsCompletedOnlyOnceItsOverrideIsNoted()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub();
        SeedWindow(fake, 60);
        var watcher = new FakeGitHub();
        var reporter = new Reporter(fake, clock: () => Now);
        Assert.Equal(0, await reporter.NoteOverrides(Locked, ct));
        fake.CloseByHand("owner/repo", 1, "alice", Now);
        await Reconciler(fake, watcher).Reconcile(Locked, ct, reporter.ClosedSeen);
        Assert.Null(Markers.Field(fake.Find("owner/repo", 1).Body, Reconciliation.Complete));
        Assert.Empty(fake.Comments);

        // Still unreconciled, so the worker asks for another cycle, which notes the override and then completes the lock.
        var next = new Reporter(fake, clock: () => Now);
        Assert.Equal(1, await next.NoteOverrides(Locked, ct));
        await Reconciler(fake, watcher).Reconcile(Locked, ct, next.ClosedSeen);
        Assert.Equal(Reconciliation.CompleteValue, Markers.Field(fake.Find("owner/repo", 1).Body, Reconciliation.Complete));
        Assert.StartsWith("`alice` closed this lock by hand", Assert.Single(fake.Comments));

        // Once complete, the closure is never judged again: its closer is not asked for.
        var closedByReads = fake.ClosedByReads;
        Assert.Equal(0, await new Reporter(fake, clock: () => Now).NoteOverrides(Locked, ct));
        Assert.Equal(closedByReads, fake.ClosedByReads);
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

    // TS-S11, ADR-010 and C-7: the hourly sweep is what notices a trigger worker that has stopped starting cycles.
    const string AlertRepo = "watcher/repo";
    static Sweep Sweeping(FakeGitHub target, FakeGitHub watcher) =>
        new(target, watcher, AlertRepo, new Alerts(watcher, AlertRepo), new WorkFinder(() => Now), () => Now);
    static Issue? Alert(FakeGitHub watcher, string title) =>
        watcher.Issues.GetValueOrDefault(AlertRepo, []).Select(i => i.Issue).SingleOrDefault(i => i.Title == title);
    const string WorkerDownTitle = "Trigger worker appears down (work waiting on owner/repo)";

    [Theory]
    // An eligible head whose push is older than the threshold: the worker had a minute to start a cycle and did not.
    [InlineData(40, true)]
    [InlineData(5, false)]
    public async Task SweepReportsWorkThatWaitedLongerThanTheWorkerShouldTake(int minutes, bool reported)
    {
        var target = new FakeGitHub { Activity = { new("before", "head", Now.AddMinutes(-minutes), "push", "alice") } };
        var watcher = new FakeGitHub();
        var work = await Sweeping(target, watcher).WorkerDown(Watched, TestContext.Current.CancellationToken);
        Assert.Equal(reported, work is not null);
        if (!reported) { Assert.Null(Alert(watcher, WorkerDownTitle)); return; }
        var alert = Alert(watcher, WorkerDownTitle);
        Assert.NotNull(alert);
        Assert.Contains("waited 40 minutes", alert.Body);
        Assert.Contains("No `watch.yml` cycle has been dispatched", alert.Body);
    }

    [Fact]
    public async Task WorkWaitingWhileCyclesAreDispatchedIsTheReportersProblemNotTheWorkers()
    {
        var target = new FakeGitHub { Activity = { new("before", "head", Now.AddHours(-3), "push", "alice") } };
        var other = new FakeGitHub { RunList = [new(9, "watch other/repo", Now.AddMinutes(-3), "completed")] };
        Assert.NotNull(await Sweeping(target, other).WorkerDown(Watched, TestContext.Current.CancellationToken));
        var watcher = new FakeGitHub { RunList = [new(9, "watch owner/repo", Now.AddMinutes(-3), "completed")] };
        Assert.Null(await Sweeping(target, watcher).WorkerDown(Watched, TestContext.Current.CancellationToken));
        Assert.Null(Alert(watcher, WorkerDownTitle));
    }

    [Fact]
    public async Task UndatedWorkIsNeverReportedAsOldButAnUnreadableRunListStillAlerts()
    {
        // A deleted target run: the report is owed, but nothing says since when.
        var deleted = new FakeGitHub { RunDeleted = true, CheckList = { new(7, "head", "in_progress", null, Now.AddHours(-2), null, "42") } };
        Assert.Null(await Sweeping(deleted, new()).WorkerDown(Watched, TestContext.Current.CancellationToken));
        var target = new FakeGitHub { Activity = { new("before", "head", Now.AddHours(-3), "push", "alice") } };
        var watcher = new FakeGitHub { RunsError = true };
        Assert.NotNull(await Sweeping(target, watcher).WorkerDown(Watched, TestContext.Current.CancellationToken));
        Assert.Contains("could not be read", Alert(watcher, WorkerDownTitle)!.Body);
    }

    // The head's own wait for poll_interval is the rule working, not the worker failing (ADR-017).
    [Fact]
    public async Task AnEligibleHeadIsDatedByItsPushOrTheIntervalItStillHadToWait()
    {
        var target = new FakeGitHub
        {
            CheckList = { Result(1, 'a', "success", 30) },
            Activity = { new("before", "head", Now.AddMinutes(-90), "push", "alice") }
        };
        var watched = new Target { Repo = "owner/repo", PollInterval = 30 };
        var work = (await new WorkFinder(() => Now).Find(watched, target, TestContext.Current.CancellationToken)).Work;
        Assert.Equal(Now, work!.Since);
        // Without the push in the activity read, the work is not dated at all.
        target.Activity.Clear();
        Assert.Null((await new WorkFinder(() => Now).Find(watched, target, TestContext.Current.CancellationToken)).Work!.Since);
    }

    // ADR-008 point 3: a secondary signal, since a gate that cannot reach the API usually cannot report through it either.
    [Fact]
    public async Task SweepReportsEachSetOfGateFailOpensOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = new FakeGitHub
        {
            Activity = { new("before", "head", Now.AddMinutes(-1), "push", "alice") },
            FailOpenRuns =
            {
                new(5, Sha('a'), "gh-readonly-queue/main/pr-1", Now.AddMinutes(-20)),
                // Older than the window the sweep covers.
                new(4, Sha('b'), "gh-readonly-queue/main/pr-0", Now.AddHours(-3))
            }
        };
        var watcher = new FakeGitHub();
        var sweep = Sweeping(target, watcher);
        Assert.Equal(1, await sweep.GateFailedOpen(Watched, ct));
        var alert = Alert(watcher, "Gate failed open on owner/repo");
        Assert.NotNull(alert);
        Assert.Contains("run 5", alert.Body);
        Assert.DoesNotContain("run 4", alert.Body);
        // The same set again says nothing new; a further fail-open is a comment on the same thread.
        Assert.Equal(1, await sweep.GateFailedOpen(Watched, ct));
        Assert.Empty(watcher.Find(AlertRepo, alert.Number).Comments);
        target.FailOpenRuns.Add(new(6, Sha('c'), "gh-readonly-queue/main/pr-2", Now.AddMinutes(-5)));
        Assert.Equal(2, await sweep.GateFailedOpen(Watched, ct));
        Assert.Contains("run 6", Assert.Single(watcher.Find(AlertRepo, alert.Number).Comments).Body);
    }

    [Fact]
    public async Task NoGateFailOpenRaisesNothing()
    {
        var watcher = new FakeGitHub();
        Assert.Equal(0, await Sweeping(new(), watcher).GateFailedOpen(Watched, TestContext.Current.CancellationToken));
        Assert.Empty(watcher.Issues);
    }

    // TS-U15: the stale-run lifecycle (ADR-013 point 5). The target's timeout is 30 minutes, so its job's run deadline is
    // its start plus 30 + 20 (the reusable workflow's margin) + 10 (the grace) = 60 minutes.
    static readonly Target Stale = new() { Repo = "owner/repo", Timeout = 30, PollInterval = 15 };
    static CheckRun Testing(int age, string? summary = null) =>
        new(7, "head", "in_progress", null, Now.AddMinutes(-age), null, "41", null, summary);
    static WorkflowJob Queued() => new("tests / main-watcher", "queued", []);
    static WorkflowJob Running(int started, params JobStep[] steps) =>
        new("tests / main-watcher", "in_progress", steps, null, Now.AddMinutes(-started));
    static string Recorded(params (string Name, int MinutesAgo)[] fields) =>
        Markers.Set("", [.. fields.Select(f => (f.Name, Markers.Stamp(Now.AddMinutes(-f.MinutesAgo))))]);
    static Planner Stopper(FakeGitHub fake, FakeGitHub? watcher = null, int minutes = 0,
        TimeSpan? queueDeadline = null, bool cancels = true) =>
        new(fake, () => Now.AddMinutes(minutes), alerts: watcher is null ? null : new Alerts(watcher, AlertRepo),
            queueDeadline: queueDeadline, cancelsRuns: cancels);

    [Fact]
    public async Task QueueDeadlineRunsFromTheCheckRunAndTheRunDeadlineFromTheJobsStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var waiting = new FakeGitHub { JobList = [Queued()] };
        Assert.Null(await Stopper(waiting).Stop(Stale, Testing(29), ct));
        Assert.Empty(waiting.Order);

        var late = new FakeGitHub { JobList = [Queued()] };
        Assert.Contains("passed its queue deadline", await Stopper(late).Stop(Stale, Testing(30), ct));
        // The time is recorded before the cancel, and the check run is not completed: it stays in progress until the run stops.
        Assert.Equal(["output:7", "cancel:41"], late.Order);
        Assert.Equal(Planner.StaleTitle, late.Title);
        Assert.Equal(Markers.Stamp(Now), Markers.Field(late.Summary, StaleRun.CancelRequested));

        // Waiting for a runner is not running: a job that started late is judged from its own start, not the check run's age.
        var started = new FakeGitHub { JobList = [Running(59)] };
        Assert.Null(await Stopper(started).Stop(Stale, Testing(180), ct));
        Assert.Empty(started.Order);
        var overrun = new FakeGitHub { JobList = [Running(60)] };
        Assert.Contains("passed its run deadline", await Stopper(overrun).Stop(Stale, Testing(180), ct));
        Assert.Equal(["output:7", "cancel:41"], overrun.Order);

        // A started job GitHub gives no start time for is never cancelled: it may still be testing.
        var undated = new FakeGitHub { JobList = [new("tests / main-watcher", "in_progress", [])] };
        Assert.Null(await Stopper(undated).Stop(Stale, Testing(600), ct));
        Assert.Empty(undated.Order);
    }

    // TS-U15: the run deadline counts from the timeout the run was dispatched with, which the Planner records on the check
    // run, never from targets.yml as it reads later. Editing a target's timeout must not move a running job's deadline.
    [Theory]
    // Dispatched at 120 minutes: the deadline is the job's start + 120 + 20 + 10, whatever the target says now.
    [InlineData(120, 30, 149, false)]
    [InlineData(120, 30, 150, true)]
    [InlineData(120, 340, 150, true)]
    // With nothing recorded — a check run from before the Planner wrote it — the current setting is all there is.
    [InlineData(null, 30, 59, false)]
    [InlineData(null, 30, 60, true)]
    public async Task TheRunDeadlineUsesTheTimeoutTheRunWasDispatchedWith(int? recorded, int configured, int ran, bool stale)
    {
        var ct = TestContext.Current.CancellationToken;
        var summary = recorded is { } minutes
            ? Markers.Set("", (StaleRun.TimeoutMinutes, minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            : null;
        var check = Testing(600, summary);
        var target = new Target { Repo = "owner/repo", Timeout = configured, PollInterval = 15 };
        var fake = new FakeGitHub { JobList = [Running(ran)] };
        Assert.Equal(stale, await Stopper(fake).Stop(target, check, ct) is not null);
        Assert.Equal(stale ? ["output:7", "cancel:41"] : [], fake.Order);
        // The worker reads the same check run, so it flags exactly what the Planner would cancel (TS-U5 (c)).
        var worker = new FakeGitHub { CheckList = [check], JobList = [Running(ran)] };
        Assert.Equal(stale, (await new WorkFinder(() => Now).Find(target, worker, ct)).Work is not null);
    }

    [Fact]
    public async Task ThePlannerRecordsTheDispatchedTimeoutAndCarriesItThroughEveryStopWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub();
        var target = new Target { Repo = "owner/repo", Timeout = 120, PollInterval = 15 };
        var started = await new Planner(fake, () => Now).Plan(target, false, ct);
        Assert.Equal("120", Markers.Field(started!.Summary, StaleRun.TimeoutMinutes));
        Assert.Equal(Planner.TestingTitle, fake.Title);

        // The cancel and force-cancel outputs replace that one, so each must carry the value forward: it is the only record
        // of what the deadline was counted from.
        var queued = new FakeGitHub { JobList = [Queued()] };
        await Stopper(queued).Stop(target, Testing(30, fake.Summary), ct);
        Assert.Equal("120", Markers.Field(queued.Summary, StaleRun.TimeoutMinutes));
        var forcing = new FakeGitHub { JobList = [Queued()] };
        await Stopper(forcing, minutes: 15).Stop(target, Testing(45, queued.Summary), ct);
        Assert.Equal("120", Markers.Field(forcing.Summary, StaleRun.TimeoutMinutes));
        Assert.NotNull(Markers.Time(forcing.Summary, StaleRun.ForceCancelRequested));
    }

    [Fact]
    public async Task CancelIsRepeatedAndThenForcedFifteenMinutesLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var waiting = new FakeGitHub { JobList = [Queued()] };
        Assert.Contains("has not stopped", await Stopper(waiting).Stop(Stale, Testing(44, Recorded((StaleRun.CancelRequested, 14))), ct));
        // Nothing is recorded again: the wait runs from the time already written, not from this cycle.
        Assert.Equal(["cancel:41"], waiting.Order);

        var forced = new FakeGitHub { JobList = [Queued()] };
        Assert.Contains("outlived its cancel", await Stopper(forced).Stop(Stale, Testing(45, Recorded((StaleRun.CancelRequested, 15))), ct));
        Assert.Equal(["output:7", "force-cancel:41"], forced.Order);
        Assert.Equal(Markers.Stamp(Now.AddMinutes(-15)), Markers.Field(forced.Summary, StaleRun.CancelRequested));
        Assert.Equal(Markers.Stamp(Now), Markers.Field(forced.Summary, StaleRun.ForceCancelRequested));
    }

    [Fact]
    public async Task AnUnstoppableRunAlertsOnceAndKeepsForceCancelling()
    {
        var ct = TestContext.Current.CancellationToken;
        var watcher = new FakeGitHub();
        var early = new FakeGitHub { JobList = [Queued()] };
        var pending = Recorded((StaleRun.CancelRequested, 45), (StaleRun.ForceCancelRequested, 14));
        Assert.Contains("has not stopped", await Stopper(early, watcher).Stop(Stale, Testing(80, pending), ct));
        Assert.Equal(["force-cancel:41"], early.Order);
        Assert.Empty(watcher.Issues);

        var check = Testing(80, Recorded((StaleRun.CancelRequested, 45), (StaleRun.ForceCancelRequested, 15)));
        var late = new FakeGitHub { JobList = [Queued()] };
        Assert.NotNull(await Stopper(late, watcher).Stop(Stale, check, ct));
        var alert = Alert(watcher, "Target run could not be stopped on owner/repo");
        Assert.NotNull(alert);
        Assert.Contains("no test starts for `owner/repo`", alert.Body);
        Assert.Contains("<!-- main-watcher unstoppable check=7 -->", alert.Body);
        // Every later cycle asks again but says nothing more, and the check run is never completed.
        Assert.NotNull(await Stopper(late, watcher).Stop(Stale, check, ct));
        Assert.Equal(["force-cancel:41", "force-cancel:41"], late.Order);
        Assert.Empty(watcher.Find(AlertRepo, alert.Number).Comments);
        Assert.Null(late.Conclusion);
    }

    [Fact]
    public async Task AStoppedRunIsJudgedByTheOutcomeTableAndADeletedOneIsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var check = Testing(80, Recorded((StaleRun.CancelRequested, 20), (StaleRun.ForceCancelRequested, 5)));
        async Task<FakeGitHub> Reported(FakeGitHub fake)
        {
            // Stopping is over once the job has completed, whatever the markers still say; the table decides from here.
            Assert.Null(await Stopper(fake).Stop(Stale, check, ct));
            Assert.True(await new Reporter(fake, new Alerts(new FakeGitHub(), AlertRepo), clock: () => Now).Report(Stale, check, ct));
            return fake;
        }
        // The cancellation cut the tests short, so there is no marker step: an infrastructure error, never a lock.
        var cut = await Reported(new() { JobList = [new("tests / main-watcher", "completed",
            [new("main-watcher-test", "cancelled"), new("main-watcher-tests-finished", "cancelled")])] });
        Assert.Equal("neutral", cut.Conclusion);
        Assert.Equal(Outcomes.Title(OutcomeKind.InfrastructureError), cut.Title);
        // The step conclusions alone would not say the watcher stopped this run.
        Assert.Contains($"cancelled this run at {Markers.Stamp(Now.AddMinutes(-20))}", cut.Summary);

        // The tests had finished before the run was stopped, so the failure still stands.
        Assert.Equal("failure", (await Reported(new())).Conclusion);

        // Deleting the run releases the target at once.
        var deleted = await Reported(new() { RunDeleted = true });
        Assert.Equal("neutral", deleted.Conclusion);
        Assert.Equal(Outcomes.Title(OutcomeKind.Unknown), deleted.Title);
    }

    [Fact]
    public async Task AJobsApiErrorChangesNothingAndACrashResumesFromTheRecordedTimes()
    {
        var ct = TestContext.Current.CancellationToken;
        var error = new FakeGitHub { JobsError = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => Stopper(error).Stop(Stale, Testing(60), ct));
        Assert.Empty(error.Order);

        // The cancel POST was refused, but the time was recorded before it, so the next cycle continues from there.
        var first = new FakeGitHub { JobList = [Queued()], CancelRefusal = "HTTP 500" };
        Assert.Contains("cancel refused: HTTP 500.", await Stopper(first).Stop(Stale, Testing(30), ct));
        var second = new FakeGitHub { JobList = [Queued()] };
        Assert.Contains("has not stopped", await Stopper(second, minutes: 14).Stop(Stale, Testing(44, first.Summary), ct));
        Assert.Equal(["cancel:41"], second.Order);
        // Fifteen minutes after the recorded time, not after the cycle that found it.
        var third = new FakeGitHub { JobList = [Queued()] };
        Assert.Contains("outlived its cancel", await Stopper(third, minutes: 15).Stop(Stale, Testing(45, first.Summary), ct));
        Assert.Equal(["output:7", "force-cancel:41"], third.Order);
    }

    [Fact]
    public async Task NoTestStartsForATargetWhoseRunIsBeingStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var check = Testing(60, Recorded((StaleRun.CancelRequested, 30)));
        // A newer head, and a forced dispatch, which lifts only the neutral cap: neither starts a second test (ADR-013).
        var fake = new FakeGitHub { CheckList = [check], Head = "newer" };
        Assert.Null(await Stopper(fake).Plan(Stale, false, ct));
        Assert.Null(await Stopper(fake).Plan(Stale, true, ct));
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public async Task TheSandboxSwitchesShortenTheQueueDeadlineAndRefuseEveryCancel()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeGitHub { JobList = [Queued()] };
        Assert.Null(await Stopper(fake).Stop(Stale, Testing(10), ct));
        Assert.Contains("passed its queue deadline",
            await Stopper(fake, queueDeadline: TimeSpan.FromMinutes(10), cancels: false).Stop(Stale, Testing(10), ct));
        // The switch makes the cancel fail without asking GitHub, so TS-S16 (h) can reach the alert.
        Assert.Equal(["output:7"], fake.Order);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("0", 30)]
    [InlineData("31", 30)]
    [InlineData("ten", 30)]
    [InlineData("10", 10)]
    public void TheQueueDeadlineSettingTakesOneToThirtyMinutes(string? value, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), StaleRun.ConfiguredQueueDeadline(value));

    // A sandbox switch applies to every target, or to one when written owner/repo=value, so the scenario suite can fault one
    // target while other scenarios run beside it (MainWatcher#25).
    [Theory]
    [InlineData(null, "")]
    [InlineData("create", "create")]
    [InlineData("comment, update", "comment|update")]
    [InlineData("owner/repo=create,owner/other=close", "create")]
    [InlineData("Owner/Repo=check:neutral , renew", "check:neutral|renew")]
    [InlineData("owner/other=true", "")]
    [InlineData("owner/repo=,=x", "")]
    public void ASandboxSwitchAppliesToEveryTargetOrToTheOneItNames(string? setting, string expected) =>
        Assert.Equal(expected, string.Join('|', SandboxSwitch.For(setting, "owner/repo")));

    [Theory]
    [InlineData("owner/other=5,owner/repo=10", 10)]
    [InlineData("owner/other=5", 30)]
    [InlineData("12", 12)]
    public void TheQueueDeadlineSettingCanNameATarget(string setting, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), StaleRun.ConfiguredQueueDeadline(setting, "owner/repo"));

    [Fact]
    public void MarkersKeepTheOtherFieldsOfTheLastMarker()
    {
        const string body = "text\n\n<!-- main-watcher first_red=abc lease_until=2026-09-17T00:00:00Z reported_check=1 -->";
        var set = Markers.Set(body, ("reported_check", "2"), ("reported_sha", "def"));
        Assert.Equal("abc", Markers.Field(set, "first_red"));
        Assert.Equal("2", Markers.Field(set, "reported_check"));
        Assert.Equal("def", Markers.Field(set, "reported_sha"));
        Assert.Equal(DateTimeOffset.Parse("2026-09-17T00:00:00Z"), Markers.Time(set, "lease_until"));
        Assert.Null(Markers.Time(set, "first_red"));
        Assert.Null(Markers.Time(null, "lease_until"));
        Assert.Equal("b", Markers.Field(Markers.Set(null, ("a", "b")), "a"));
    }

    // #60: every target's cycles share the main-watcher installation's budget, and a fourth scenario-suite run spent it with no
    // alert, since only the worker's own Apps were watched.
    [Fact]
    public async Task ALowInstallationBudgetRaisesOneAlertPerWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var watcher = new FakeGitHub();
        var alerts = new Alerts(watcher, "owner/watcher", () => Now);
        var reset = DateTimeOffset.Parse("2026-09-22T14:35:41Z");
        Assert.Null(await InstallationBudget.Judge("owner/repo", new("core", 1000, 5000, reset), null, alerts, Now, ct));
        Assert.Null(await InstallationBudget.Judge("owner/repo", null, null, alerts, Now, ct));
        Assert.False(watcher.Issues.ContainsKey("owner/watcher"));

        Assert.Equal(InstallationBudget.LowTitle,
            await InstallationBudget.Judge("owner/repo", new("core", 999, 5000, reset), null, alerts, Now, ct));
        // Another target in the same window, on a budget refilling two seconds later, as GitHub answered one sweep (#60).
        await InstallationBudget.Judge("owner/other", new("core", 700, 5000, reset.AddSeconds(2)), null, alerts, Now, ct);
        var alert = Assert.Single(watcher.Issues["owner/watcher"]);
        Assert.Equal(InstallationBudget.LowTitle, alert.Issue.Title);
        Assert.Contains("left 999 of 5000 `core` requests (20%)", alert.Issue.Body);
        Assert.Contains("refills at 2026-09-22 14:35:41 UTC", alert.Issue.Body);
        Assert.Empty(alert.Comments);

        // The next window is one comment on the same alert.
        await InstallationBudget.Judge("owner/repo", new("core", 10, 5000, reset.AddHours(1)), null, alerts, Now, ct);
        Assert.Contains("left 10 of 5000", Assert.Single(Assert.Single(watcher.Issues["owner/watcher"]).Comments).Body);
    }

    [Fact]
    public async Task ACycleRefusedByTheRateLimitRaisesAnAlertNamingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var watcher = new FakeGitHub();
        var refused = "GitHub answered 403 (Forbidden) to GET /repos/owner/repo/commits/main: API rate limit exceeded for installation ID 1";
        Assert.Equal(InstallationBudget.RefusedTitle, await InstallationBudget.Judge("owner/repo",
            new("core", 0, 5000, DateTimeOffset.Parse("2026-09-21T22:15:29Z")), refused,
            new Alerts(watcher, "owner/watcher", () => Now), Now, ct));
        var alert = Assert.Single(watcher.Issues["owner/watcher"]);
        Assert.Equal(InstallationBudget.RefusedTitle, alert.Issue.Title);
        Assert.Contains($"A cycle for `owner/repo` was refused by GitHub's rate limit:\n\n> {refused}", alert.Issue.Body);
        Assert.Contains("The lowest budget it saw was 0 of 5000 requests left, refilled at 22:15:29Z.", alert.Issue.Body);
        Assert.Equal(Alerts.Label, alert.Label);
    }

    // Cycles for different targets, and a sweep's legs, run at once, and each checks before it writes, so every one of them
    // can find nothing written (PR #62 review). Here the issue and comment lists lag, so none of the three sees another's
    // write at all: only the claim decides, and only one alert is ever written, so only one is ever notified. They also run
    // either side of a minute boundary, which a claim named after the claiming cycle's own minute let through (PR #62 review).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCyclesWriteTheBudgetAlertOnce(bool alreadyOpen)
    {
        var ct = TestContext.Current.CancellationToken;
        var watcher = new FakeGitHub { ListLags = true };
        if (alreadyOpen) watcher.Seed("owner/watcher", Alerts.Label, InstallationBudget.LowTitle, "github-actions[bot]", "Bot", "earlier window");
        var reset = DateTimeOffset.Parse("2026-09-22T14:35:41Z");
        // One second before a minute ends, and two and three seconds after it.
        var minute = Now.AddSeconds(-Now.Second).AddSeconds(59);
        var cycles = new[] { minute, minute.AddSeconds(3), minute.AddSeconds(4) }.Select((at, i) =>
            InstallationBudget.Judge($"owner/target-{i}", new("core", 900, 5000, reset), null,
                new Alerts(watcher, "owner/watcher", () => at), at, ct)).ToArray();
        await Task.WhenAll(cycles);

        var alert = Assert.Single(watcher.Issues["owner/watcher"]);
        Assert.True(alert.Open);
        Assert.Equal(alreadyOpen ? 1 : 0, alert.Comments.Count);
        Assert.Equal(1, watcher.Order.Count(o => o.StartsWith("create:", StringComparison.Ordinal) || o.StartsWith("comment:", StringComparison.Ordinal)));
        Assert.NotEqual(minute.Minute, minute.AddSeconds(3).Minute); // The cycles did straddle a minute boundary.
        var claim = Assert.Single(watcher.CreatedLabels["owner/watcher"]);
        Assert.StartsWith($"{Alerts.ClaimedAt}{Markers.Stamp(minute)}", claim.Description);

        // An hour later the list has caught up, as GitHub's does in seconds. The claim names the alert and its window, so the
        // next window is raised, on the same alert, and claims older than a day are deleted.
        watcher.Catch();
        await watcher.Claim("owner/watcher", $"{Alerts.ClaimPrefix}0badcafe", $"{Alerts.ClaimedAt}{Markers.Stamp(Now - Alerts.ClaimKept - TimeSpan.FromMinutes(1))}.", ct);
        await InstallationBudget.Judge("owner/later", new("core", 900, 5000, reset.AddHours(1)), null,
            new Alerts(watcher, "owner/watcher", () => Now), Now, ct);
        Assert.Equal(alreadyOpen ? 2 : 1, Assert.Single(watcher.Issues["owner/watcher"]).Comments.Count);
        Assert.Contains(watcher.CreatedLabels["owner/watcher"], l => l.Name == claim.Name);
        Assert.DoesNotContain(watcher.CreatedLabels["owner/watcher"], l => l.Name == $"{Alerts.ClaimPrefix}0badcafe");
        Assert.Equal(2, watcher.CreatedLabels["owner/watcher"].Count(l => l.Name.StartsWith(Alerts.ClaimPrefix, StringComparison.Ordinal)));
    }

    // A claim stands for an alert that was written, so one whose write fails is given back (PR #62 review).
    [Fact]
    public async Task AClaimWhoseWriteFailsIsGivenBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var watcher = new FakeGitHub { IssueError = true };
        var budget = new RateLimit("core", 900, 5000, DateTimeOffset.Parse("2026-09-22T14:35:41Z"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            InstallationBudget.Judge("owner/repo", budget, null, new Alerts(watcher, "owner/watcher", () => Now), Now, ct));
        Assert.Empty(watcher.CreatedLabels["owner/watcher"]);
        Assert.False(watcher.Issues.ContainsKey("owner/watcher"));
        // The next cycle claims the same window again and writes the alert.
        watcher.IssueError = false;
        await InstallationBudget.Judge("owner/repo", budget, null, new Alerts(watcher, "owner/watcher", () => Now), Now, ct);
        Assert.Equal(InstallationBudget.LowTitle, Assert.Single(watcher.Issues["owner/watcher"]).Issue.Title);
    }

    // TS-U16: the onboarding dry run (FR-1). It checks the entry, the two copied workflows and one real test, validates the
    // CTRF that test uploads, and writes nothing to the target but the dispatch, so nothing it does can lock the repository.

    const string GatePath = "owner/repo:.github/workflows/main-watcher-gate.yml";
    const string GateBody = "jobs:\n  gate:\n    steps:\n      - uses: Actium-Group-Corporation/MainWatcher/.github/actions/gate@v1\n";

    static async Task<DryRunReport> Dry(FakeGitHub fake, Target? target = null)
    {
        // The wait advances the clock, so a check that polls to its deadline reaches it in the test rather than spinning.
        var clock = Now;
        return await new DryRun(fake, () => clock, (d, _) => { clock += d; return Task.CompletedTask; })
            .Run(target ?? new Target { Repo = "owner/repo" }, TestContext.Current.CancellationToken);
    }

    /// <summary>A target set up correctly, whose suite passes.</summary>
    static FakeGitHub GreenTarget()
    {
        var fake = new FakeGitHub { JobConclusion = "success", ReportResult = new(true, []) };
        fake.Files[GatePath] = GateBody;
        return fake;
    }

    [Fact]
    public async Task DryRunChecksTheWholeTestPathAndWritesNothingButTheDispatch()
    {
        var fake = GreenTarget();
        var report = await Dry(fake, new Target { Repo = "owner/repo", Enabled = false, Timeout = 12 });

        Assert.True(report.Passed);
        Assert.Equal(["Target entry", "Test caller", "Gate workflow", "Test run", "Test outcome", "CTRF reports"],
            report.Steps.Select(s => s.Name));
        Assert.Contains("`timeout` 12 min", report.Steps[0].Detail);
        // A dry run is made before the entry is enabled, so a disabled one is reported, never refused.
        Assert.Contains("`enabled` false", report.Steps[0].Detail);
        Assert.Contains("run 77", report.Steps[3].Detail);
        Assert.Contains("the tests passed", report.Steps[4].Detail);
        Assert.Contains("validates against the CTRF schema", report.Steps[5].Detail);
        // No check run and no issue: the one write is the dispatch, with the check run ID a run by hand leaves empty.
        Assert.Equal("dispatch-workflow:main-watcher-tests.yml:sha=head,check_run_id=", Assert.Single(fake.Writes));
        Assert.Empty(fake.Order);
    }

    [Fact]
    public async Task DryRunPassesOnFailingTestsAndSaysTheLockWouldOpen()
    {
        var fake = new FakeGitHub { JobConclusion = "failure" };
        fake.Files[GatePath] = GateBody;
        var report = await Dry(fake);

        // The contract held end to end, which is what a dry run judges. That `main` is red is a different thing, and is said
        // where it will be read rather than folded into the verdict.
        Assert.True(report.Passed);
        Assert.Contains("the tests **failed**", report.Steps[4].Detail);
        Assert.Contains("`main-broken`", report.Steps[4].Detail);
        Assert.Contains("`Alpha`", report.Steps[5].Detail);
    }

    [Fact]
    public async Task DryRunStopsAtACallerMismatchWithoutDispatching()
    {
        var fake = new FakeGitHub { InvalidCaller = true };
        fake.Files[GatePath] = GateBody;
        var report = await Dry(fake);

        Assert.False(report.Passed);
        Assert.Equal("Test caller", report.Steps[^1].Name);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public async Task DryRunStopsWhenTheGateWorkflowIsMissingOrDoesNotRunTheAction()
    {
        var missing = await Dry(new FakeGitHub());
        Assert.False(missing.Passed);
        Assert.Equal("Gate workflow", missing.Steps[^1].Name);
        Assert.Contains("is missing", missing.Steps[^1].Detail);

        var wrong = new FakeGitHub();
        wrong.Files[GatePath] = "jobs:\n  gate:\n    steps:\n      - run: exit 0\n";
        var report = await Dry(wrong);
        Assert.False(report.Passed);
        Assert.Contains("does not run", report.Steps[^1].Detail);
        Assert.Empty(wrong.Writes);
    }

    [Fact]
    public async Task DryRunFailsWhenTheDispatchedRunNeverAppears()
    {
        var fake = new FakeGitHub { DispatchStartsRun = false };
        fake.Files[GatePath] = GateBody;
        var report = await Dry(fake);

        Assert.False(report.Passed);
        Assert.Equal("Test run", report.Steps[^1].Name);
        Assert.Contains("No run of `main-watcher-tests.yml` named `head` appeared", report.Steps[^1].Detail);
    }

    [Fact]
    public async Task DryRunRefusesToGuessBetweenTwoRunsOfTheCommit()
    {
        var fake = new FakeGitHub { DispatchedRuns = 2 };
        fake.Files[GatePath] = GateBody;
        var report = await Dry(fake);

        Assert.False(report.Passed);
        Assert.Equal("Test run", report.Steps[^1].Name);
        Assert.Contains("2 runs", report.Steps[^1].Detail);
    }

    [Fact]
    public async Task DryRunFailsOnANeutralOutcome()
    {
        // The marker step is missing, so the tests did not run to completion (ADR-013): no row of the outcome table trusts the
        // test step, and a target left like this would give the watcher neutral results for ever.
        var fake = new FakeGitHub { JobList = [new("main-watcher", "completed", [new("main-watcher-test", "failure")])] };
        fake.Files[GatePath] = GateBody;
        var report = await Dry(fake);

        Assert.False(report.Passed);
        Assert.Equal("Test outcome", report.Steps[^1].Name);
        Assert.Contains("Infrastructure error", report.Steps[^1].Detail);
    }

    [Fact]
    public async Task DryRunFailsWhenTheJobNeverCompletes()
    {
        var fake = new FakeGitHub { JobList = [new("main-watcher", "in_progress", [])] };
        fake.Files[GatePath] = GateBody;
        var report = await Dry(fake, new Target { Repo = "owner/repo", Timeout = 10 });

        Assert.False(report.Passed);
        Assert.Equal("Test outcome", report.Steps[^1].Name);
        // The target's own timeout, plus the reusable workflow's margin and the queue's — well inside the token's hour.
        Assert.Contains("had not completed 40 minutes", report.Steps[^1].Detail);
    }

    [Fact]
    public async Task DryRunJudgesACompletedRunWithNoTestJobAtOnce()
    {
        // The gateway answers a run whose job GitHub has not created yet with a queued job, so an empty list is only ever a
        // completed run with no `main-watcher` job. Waiting that out would spend the whole timeout and then blame the timeout.
        var fake = new FakeGitHub { JobList = [] };
        fake.Files[GatePath] = GateBody;
        var report = await Dry(fake, new Target { Repo = "owner/repo", Timeout = 10 });

        Assert.False(report.Passed);
        Assert.Equal("Test outcome", report.Steps[^1].Name);
        Assert.Contains("no `main-watcher` job", report.Steps[^1].Detail);
        Assert.DoesNotContain("had not completed", report.Steps[^1].Detail);
    }

    [Fact]
    public async Task DryRunStopsWithinItsTokensHourRatherThanOutlastingIt()
    {
        // A 340-minute target would otherwise be waited on for hours with a token that dies after one, failing authentication
        // part-way through judging and reporting that instead of the setup.
        var fake = new FakeGitHub { JobList = [new("main-watcher", "in_progress", [])] };
        fake.Files[GatePath] = GateBody;
        var report = await Dry(fake, new Target { Repo = "owner/repo", Timeout = 340 });

        Assert.False(report.Passed);
        Assert.Equal("Test outcome", report.Steps[^1].Name);
        Assert.Contains("still going after 50 minutes", report.Steps[^1].Detail);
        Assert.Contains("App token", report.Steps[^1].Detail);
        // Nothing is known to be wrong, so it says where to look rather than blaming the target's timeout.
        Assert.DoesNotContain("had not completed", report.Steps[^1].Detail);
    }

    [Fact]
    public async Task DryRunFailsWhenTheReportsAreNotValidCtrf()
    {
        var fake = new FakeGitHub { JobConclusion = "success", ReportResult = CtrfResult.Unknown };
        fake.Files[GatePath] = GateBody;
        var report = await Dry(fake);

        Assert.False(report.Passed);
        Assert.Equal("CTRF reports", report.Steps[^1].Name);
        Assert.Contains("not valid CTRF", report.Steps[^1].Detail);
    }

    [Fact]
    public async Task DryRunReportsTheCheckAnApiErrorStoppedRatherThanThrowing()
    {
        var fake = new FakeGitHub { RunsError = true };
        fake.Files[GatePath] = GateBody;
        var report = await Dry(fake);

        Assert.False(report.Passed);
        Assert.Equal("Test run", report.Steps[^1].Name);
        Assert.Contains("unavailable", report.Steps[^1].Detail);
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
        public bool IssueError { get; set; }
        /// <summary>
        /// The issue list lags, as GitHub's does: an issue or comment this fake creates is not listed until <see cref="Catch"/>.
        /// </summary>
        public bool ListLags { get; set; }
        readonly HashSet<int> unlisted = [];
        readonly HashSet<long> unlistedComments = [];
        public void Catch()
        {
            unlisted.Clear();
            unlistedComments.Clear();
        }
        public string JobConclusion { get; init; } = "failure";
        /// <summary>Stops the report, as a crash would, right after this many issue writes succeed.</summary>
        public int? StopAfterWrites { get; set; }
        public int ClosedByReads { get; private set; }
        int next;
        int writes;
        long commentIds;

        void Wrote(string entry)
        {
            Order.Add(entry);
            if (++writes == StopAfterWrites) throw new HttpRequestException("stopped after a write");
        }

        public FakeIssue Seed(string repo, string label, string title, string author, string type, string body = "",
            DateTimeOffset? createdAt = null)
        {
            if (!Issues.TryGetValue(repo, out var list)) Issues[repo] = list = [];
            next++;
            list.Add(new(new(next, title, body, author, type, $"https://github.com/{repo}/issues/{next}", Id: 1000 + next,
                CreatedAt: createdAt), label));
            return list[^1];
        }

        public FakeIssue Find(string repo, int number) => Issues[repo].Single(i => i.Issue.Number == number);

        public void CloseByHand(string repo, int number, string? login = "alice", DateTimeOffset? at = null)
        {
            var issue = Find(repo, number);
            issue.Issue = issue.Issue with { State = "closed", StateReason = "completed", ClosedAt = at };
            issue.ClosedBy = login is null ? null : new(login, "User");
        }

        public Task<string?> File(string repo, string path, CancellationToken ct) => Task.FromResult(Files.GetValueOrDefault($"{repo}:{path}"));
        public Task<IReadOnlyList<Issue>> OpenIssues(string repo, string label, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Issue>>(Issues.GetValueOrDefault(repo, [])
                .Where(i => i.Label == label && i.Open && !unlisted.Contains(i.Issue.Number)).Select(i => i.Issue).ToArray());
        Task<IReadOnlyList<Issue>> IGitHubGateway.Issues(string repo, string label, DateTimeOffset since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Issue>>(Issues.GetValueOrDefault(repo, [])
                .Where(i => i.Label == label && (i.Issue.UpdatedAt is null || i.Issue.UpdatedAt >= since)).Select(i => i.Issue).ToArray());
        Task<IReadOnlyList<IssueComment>> IGitHubGateway.Comments(string repo, int number, DateTimeOffset? since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IssueComment>>(Find(repo, number).Comments.Where(c => !unlistedComments.Contains(c.Id)).ToArray());
        public Task<Account?> ClosedBy(string repo, int number, CancellationToken ct)
        {
            ClosedByReads++;
            return Task.FromResult(Find(repo, number).ClosedBy);
        }
        public Task<Issue> CreateIssue(string repo, string title, string body, string label, CancellationToken ct)
        {
            if (IssueError) throw new HttpRequestException("issues unavailable");
            var issue = Seed(repo, label, title, "main-watcher[bot]", "Bot", body);
            if (ListLags) unlisted.Add(issue.Issue.Number);
            Wrote($"create:{repo}");
            return Task.FromResult(issue.Issue);
        }
        public Task Comment(string repo, int number, string body, CancellationToken ct)
        {
            if (IssueError) throw new HttpRequestException("issues unavailable");
            if (Issues.TryGetValue(repo, out var list) && list.FirstOrDefault(i => i.Issue.Number == number) is { } issue)
                issue.Comments.Add(new(body, "main-watcher[bot]", "Bot", ++commentIds));
            if (ListLags) unlistedComments.Add(commentIds);
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
        /// <summary>The labels of the repository, as the claim of a shared alert creates them.</summary>
        public Dictionary<string, List<RepoLabel>> CreatedLabels { get; } = [];
        public Task<bool> Claim(string repo, string name, string description, CancellationToken ct)
        {
            CreatedLabels.TryAdd(repo, []);
            // A label name is unique in a repository: GitHub refuses the second creation, whoever sends it.
            if (CreatedLabels[repo].Any(l => l.Name == name)) return Task.FromResult(false);
            CreatedLabels[repo].Add(new(name, description));
            Wrote($"claim:{name}");
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<RepoLabel>> RepoLabels(string repo, string prefix, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RepoLabel>>(CreatedLabels.GetValueOrDefault(repo, [])
                .Where(l => l.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray());
        public Task DeleteLabel(string repo, string name, CancellationToken ct)
        {
            CreatedLabels.GetValueOrDefault(repo, []).RemoveAll(l => l.Name == name);
            Wrote($"delete-label:{name}");
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
        public string Head { get; init; } = "head";
        public Task<string> MainHead(string repo, CancellationToken ct) => Task.FromResult(Head);
        public List<CheckRun> CheckList { get; init; } = [];
        public bool ChecksError { get; set; }
        public Task<IReadOnlyList<CheckRun>> Checks(string repo, CancellationToken ct) =>
            ChecksError ? throw new HttpRequestException("checks unavailable") : Task.FromResult<IReadOnlyList<CheckRun>>(CheckList);
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
        public List<FailOpen> FailOpenRuns { get; init; } = [];
        public bool FailOpenError { get; init; }
        public Task<IReadOnlyList<FailOpen>> FailOpens(string repo, DateTimeOffset since, CancellationToken ct) =>
            FailOpenError ? throw new HttpRequestException("gate runs unavailable")
                : Task.FromResult<IReadOnlyList<FailOpen>>(FailOpenRuns.Where(f => f.At >= since).ToArray());
        public Task<CheckRun> CreateCheck(string repo, string sha, DateTimeOffset now, string title, string summary, CancellationToken ct)
        {
            Writes.Add("create");
            Title = title;
            Summary = summary;
            // GitHub returns the check run it created, output and all, which is how the Planner's caller sees the marker.
            return Task.FromResult(new CheckRun(1, sha, "in_progress", null, now, null, null, title, summary));
        }
        public Task<long?> Dispatch(string repo, string sha, long checkId, CancellationToken ct) { Writes.Add("dispatch"); if (DispatchError is not null) throw DispatchError; return Task.FromResult(DispatchId); }
        /// <summary>Whether a workflow dispatch makes a run appear, as GitHub's does, for the dry run's lookup.</summary>
        public bool DispatchStartsRun { get; init; } = true;
        /// <summary>How many runs of the dispatched commit appear: 2 is the ambiguity the dry run refuses to guess at.</summary>
        public int DispatchedRuns { get; init; } = 1;
        public Task DispatchWorkflow(string repo, string workflow, IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
        {
            Writes.Add($"dispatch-workflow:{workflow}:" + string.Join(",", inputs.Select(i => $"{i.Key}={i.Value}")));
            if (DispatchError is not null) throw DispatchError;
            if (DispatchStartsRun)
                for (var i = 0; i < DispatchedRuns; i++)
                    RunList.Add(new(77 + i, $"main-watcher-tests {inputs["sha"]}", Now, "completed"));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<WorkflowRun>> Runs(string repo, string workflow, DateTimeOffset since, CancellationToken ct) => RunsError ? throw new HttpRequestException("unavailable")
            : Task.FromResult<IReadOnlyList<WorkflowRun>>(RunList.Where(r => r.CreatedAt >= since).ToArray());
        public Task Link(string repo, long checkId, long runId, CancellationToken ct) { Writes.Add($"link:{runId}"); return Task.CompletedTask; }
        /// <summary>The run's jobs; null when the run was deleted. By default, a completed job with <see cref="JobConclusion"/> and a successful marker.</summary>
        public IReadOnlyList<WorkflowJob>? JobList { get; init; }
        public bool RunDeleted { get; init; }
        public Task<IReadOnlyList<WorkflowJob>?> Jobs(string repo, long runId, CancellationToken ct) => JobsError ? throw new HttpRequestException("unavailable")
            : Task.FromResult(RunDeleted ? null : JobList ?? [new("tests / main-watcher", "completed", [new("main-watcher-test", JobConclusion), new("main-watcher-tests-finished", "success")])]);
        public Task<CtrfResult> Reports(string repo, long runId, CancellationToken ct) => Task.FromResult(ReportResult);
        /// <summary>What GitHub says to a stop request; null means it accepted it.</summary>
        public string? CancelRefusal { get; init; }
        public Task<string?> CancelRun(string repo, long runId, bool force, CancellationToken ct)
        {
            Order.Add($"{(force ? "force-cancel" : "cancel")}:{runId}");
            return Task.FromResult(CancelRefusal);
        }
        public Task Output(string repo, long checkId, string title, string summary, CancellationToken ct)
        {
            Order.Add($"output:{checkId}");
            Title = title;
            Summary = summary;
            return Task.CompletedTask;
        }
        public string? Title { get; private set; }
        public Task Complete(string repo, long checkId, string conclusion, string title, string summary, CancellationToken ct) { Order.Add($"complete:{conclusion}"); Conclusion = conclusion; Title = title; Summary = summary; return Task.CompletedTask; }

        /// <summary>The commits one activity entry added, keyed by its <c>after</c> commit (ADR-015).</summary>
        public Dictionary<string, List<MergedCommit>> Merged { get; } = [];
        /// <summary><c>after</c> commits whose comparison cannot be read at all.</summary>
        public HashSet<string> MergedError { get; } = [];
        /// <summary><c>after</c> commits GitHub can no longer compare, as a force push leaves behind.</summary>
        public HashSet<string> Uncomparable { get; } = [];
        /// <summary><c>after</c> commits whose range holds more commits than the compare read.</summary>
        public HashSet<string> Truncated { get; } = [];
        /// <summary>Timeline events by pull request, oldest first, as GitHub returns them.</summary>
        public Dictionary<int, List<PullEvent>> Labels { get; } = [];
        /// <summary>Pull requests whose label history cannot be read (ADR-015 point 8).</summary>
        public HashSet<int> LabelsError { get; } = [];
        /// <summary>Merge groups the gate failed, for the unlock comment (R-7).</summary>
        public List<GateBlock> Blocked { get; } = [];
        public bool BlockedError { get; init; }
        public Task<MergedRange?> MergedCommits(string repo, string before, string after, int skip, CancellationToken ct)
        {
            Reads.Add($"merged:{after}:{skip}");
            if (MergedError.Contains(after)) throw new HttpRequestException("comparison unavailable");
            if (Uncomparable.Contains(after)) return Task.FromResult<MergedRange?>(null);
            // One commit a pass, when the range is marked truncated, so a test can watch it being finished across several.
            var all = Merged.GetValueOrDefault(after, []);
            var read = Truncated.Contains(after) ? all.Skip(skip).Take(1).ToArray() : [.. all.Skip(skip)];
            return Task.FromResult<MergedRange?>(new(read, skip + read.Length < all.Count));
        }
        public Task<IReadOnlyList<PullEvent>> PullEvents(string repo, int number, CancellationToken ct)
        {
            Reads.Add($"labels:{number}");
            return LabelsError.Contains(number) ? throw new HttpRequestException("label events unavailable")
                : Task.FromResult<IReadOnlyList<PullEvent>>(Labels.GetValueOrDefault(number, []).ToArray());
        }
        public Task<IReadOnlyList<GateBlock>> GateBlocks(string repo, DateTimeOffset since, CancellationToken ct) =>
            BlockedError ? throw new HttpRequestException("gate runs unavailable")
                : Task.FromResult<IReadOnlyList<GateBlock>>(Blocked.Where(b => b.At >= since).ToArray());
        /// <summary>Every read reconciliation makes, in order, so a pass that stopped can be shown to have stopped.</summary>
        public List<string> Reads { get; } = [];

        /// <summary>The merge groups still in the queue, as the sweep lists them (ADR-016).</summary>
        public List<QueuedGroup> Queued { get; } = [];
        /// <summary>The gate runs on each merge group's commit.</summary>
        public Dictionary<string, List<GateRun>> Gates { get; } = [];
        public bool QueueError { get; init; }
        /// <summary>What GitHub says to a re-run request; null means it accepted it.</summary>
        public string? RerunRefusal { get; init; }
        public Task<IReadOnlyList<QueuedGroup>> QueuedGroups(string repo, CancellationToken ct)
        {
            Reads.Add("queue");
            return QueueError ? throw new HttpRequestException("queue branches unavailable")
                : Task.FromResult<IReadOnlyList<QueuedGroup>>(Queued.ToArray());
        }
        public Task<IReadOnlyList<GateRun>> GateRuns(string repo, string sha, CancellationToken ct)
        {
            Reads.Add($"gates:{sha}");
            return Task.FromResult<IReadOnlyList<GateRun>>(Gates.GetValueOrDefault(sha, []).ToArray());
        }
        public Task<string?> Rerun(string repo, long runId, CancellationToken ct)
        {
            Order.Add($"rerun:{runId}");
            // GitHub starts a new attempt, so the run is running again and its start moves to now: the next pass sees exactly
            // that, which is what keeps one sweep from asking twice.
            if (RerunRefusal is null)
                foreach (var runs in Gates.Values)
                    for (var i = 0; i < runs.Count; i++)
                        if (runs[i].Id == runId) runs[i] = runs[i] with { Status = "in_progress", Conclusion = null, StartedAt = Rerunning };
            return Task.FromResult(RerunRefusal);
        }
        /// <summary>When a re-run attempt starts. The tests set it to their own "now".</summary>
        public DateTimeOffset Rerunning { get; set; } = DateTimeOffset.MaxValue;
    }
}

