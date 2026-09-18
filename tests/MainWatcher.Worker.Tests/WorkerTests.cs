using MainWatcher.Core;
using MainWatcher.Core.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace MainWatcher.Worker.Tests;

public class WorkerTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T19:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    const string WatcherRepo = "owner/watcher";
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static Target Watched(string repo = "owner/repo") => new() { Repo = repo, PollInterval = 15 };
    static CheckRun Pending(string? runId = "41", int age = 10, long id = 7) =>
        new(id, "head", "in_progress", null, Now.AddMinutes(-age), null, runId);
    static CheckRun Done(string conclusion = "success", string sha = "head") =>
        new(1, sha, "completed", conclusion, Now.AddHours(-2), Now.AddHours(-1), "40");
    static WorkflowJob Job(string name, string status) => new(name, status, []);
    static WorkflowJob Finished(string conclusion = "failure", DateTimeOffset? completedAt = null) => new("tests / main-watcher", "completed",
        [new("main-watcher-test", conclusion), new("main-watcher-tests-finished", "success")], completedAt);

    // TS-U13 and TS-U3, TS-U5 (a): the worker flags a head exactly when the shared rule says it is eligible. It never forces.
    [Theory]
    [MemberData(nameof(EligibilityFixtures.Unforced), MemberType = typeof(EligibilityFixtures))]
    public async Task FlagsOnlyEligibleHeads(string name)
    {
        var c = EligibilityFixtures.Named(name);
        var target = new FakeGitHub { Head = c.Head, CheckList = c.Checks.ToList() };
        var work = await new WorkFinder(() => c.Now).Find(new() { Repo = "owner/repo", PollInterval = (int)c.PollInterval.TotalMinutes }, target, Ct);
        Assert.Equal(c.Eligible, work is not null);
    }

    // TS-U5 (b): a completed main-watcher job is work whatever other jobs in the run are doing, with or without an artifact.
    [Theory]
    [InlineData("in_progress")]
    [InlineData("queued")]
    [InlineData("completed")]
    public async Task FlagsCompletedMainWatcherJobWhateverOtherJobsAreDoing(string otherJob)
    {
        var target = new FakeGitHub { CheckList = [Done(), Pending()] };
        target.JobsByRun[41] = [Job("tests / report", otherJob), Finished(), Job("tests / lint", otherJob)];
        var work = await new WorkFinder(() => Now).Find(Watched(), target, Ct);
        Assert.Contains("main-watcher job of target run 41 has completed", work!.Reason);
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("queued")]
    public async Task RunningMainWatcherJobIsNotWorkEvenWhenOtherJobsFinished(string status)
    {
        var target = new FakeGitHub { CheckList = [Pending()] };
        target.JobsByRun[41] = [Job("tests / report", "completed"), Job("tests / main-watcher", status)];
        Assert.Null(await new WorkFinder(() => Now).Find(Watched(), target, Ct));
    }

    // TS-U5 (c): a job that has not completed has two deadlines, and past either one the Planner must stop the run
    // (ADR-013 point 5). The worker flags it so a cycle runs at all.
    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    public async Task JobsThatNeverGetARunnerAreWorkAfterTheQueueDeadline(int age, bool expected)
    {
        var target = new FakeGitHub { CheckList = [Pending(age: age)] };
        target.JobsByRun[41] = [Job("tests / report", "completed"), Job("tests / main-watcher", "queued")];
        var work = await new WorkFinder(() => Now).Find(Watched(), target, Ct);
        Assert.Equal(expected, work is not null);
        if (!expected) return;
        Assert.Contains("did not start within 30 minutes", work!.Reason);
        // Dated by the deadline it passed, and not a report owed: only the Reporter's own work carries a check ID.
        Assert.Equal(Now, work.Since);
        Assert.Null(work.Check);
    }

    // The target's 30-minute timeout, the workflow's 20-minute margin and the 10-minute grace: 60 minutes from the job's start.
    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    public async Task OverrunningJobsAreWorkWhateverTheirMarkerStepShows(int started, bool expected)
    {
        var target = new FakeGitHub { CheckList = [Pending(age: 200)] };
        // The marker step has already succeeded, but the job itself has not completed, so no row of the outcome table applies.
        target.JobsByRun[41] = [new("tests / main-watcher", "in_progress",
            [new("main-watcher-test", "failure"), new("main-watcher-tests-finished", "success")], null, Now.AddMinutes(-started))];
        var work = await new WorkFinder(() => Now).Find(Watched(), target, Ct);
        Assert.Equal(expected, work is not null);
        if (expected) Assert.Contains("has run past its deadline", work!.Reason);
    }

    [Fact]
    public async Task AStopAlreadyAskedForKeepsTheRunFlaggedUntilItHasStopped()
    {
        var check = Pending(age: 40) with { Summary = Markers.Set("", (StaleRun.CancelRequested, Markers.Stamp(Now.AddMinutes(-5)))) };
        var target = new FakeGitHub { CheckList = [check] };
        // The job got a runner after its queue deadline passed, so neither deadline holds now; the run is still being stopped.
        target.JobsByRun[41] = [new("tests / main-watcher", "in_progress", [], null, Now.AddMinutes(-1))];
        var stopping = await new WorkFinder(() => Now).Find(Watched(), target, Ct);
        Assert.Contains("was asked to stop and has not stopped", stopping!.Reason);
        Assert.Equal(Now.AddMinutes(-5), stopping.Since);

        // Once it has stopped, it is a report the Reporter owes, not a stale run: the outcome table judges its steps.
        target.JobsByRun[41] = [Finished(completedAt: Now.AddMinutes(-1))];
        var owed = await new WorkFinder(() => Now).Find(Watched(), target, Ct);
        Assert.Contains("has completed", owed!.Reason);
        Assert.Equal(7, owed.Check);
    }

    [Fact]
    public async Task TheSandboxQueueDeadlineIsSharedWithThePlanner()
    {
        var target = new FakeGitHub { CheckList = [Pending(age: 10)] };
        target.JobsByRun[41] = [Job("tests / main-watcher", "queued")];
        Assert.Null(await new WorkFinder(() => Now).Find(Watched(), target, Ct));
        Assert.NotNull(await new WorkFinder(() => Now, TimeSpan.FromMinutes(10)).Find(Watched(), target, Ct));
    }

    [Fact]
    public async Task DeletedRunsAndRunsWithoutTheJobAreWorkForTheReporter()
    {
        var target = new FakeGitHub { CheckList = [Pending()] };
        target.JobsByRun[41] = null;
        Assert.Contains("was deleted", (await new WorkFinder(() => Now).Find(Watched(), target, Ct))!.Reason);
        // A completed run with no main-watcher job: the Reporter records the broken contract.
        target.JobsByRun[41] = [];
        Assert.NotNull(await new WorkFinder(() => Now).Find(Watched(), target, Ct));
    }

    [Theory]
    [InlineData(1, 5, true)]
    [InlineData(2, 5, false)]
    [InlineData(0, 29, false)]
    [InlineData(0, 30, true)]
    public async Task UnlinkedChecksAreWorkOnlyWhenRecoveryCanAct(int matchingRuns, int age, bool expected)
    {
        var check = Pending(runId: null, age: age);
        var target = new FakeGitHub
        {
            CheckList = [check],
            RunList = [.. Enumerable.Range(0, matchingRuns).Select(i => new WorkflowRun(50 + i, "main-watcher-tests head", check.StartedAt, "queued")),
                new WorkflowRun(60, "main-watcher-tests other", check.StartedAt, "queued")]
        };
        Assert.Equal(expected, await new WorkFinder(() => Now).Find(Watched(), target, Ct) is not null);
        Assert.Equal(["runs:owner/repo:main-watcher-tests.yml"], target.Reads);
    }

    [Fact]
    public async Task NothingIsWorkForATestedIdleHead()
    {
        var target = new FakeGitHub { CheckList = [Done("failure"), Done("success", "old")] };
        Assert.Null(await new WorkFinder(() => Now).Find(Watched(), target, Ct));
    }

    sealed class Setup
    {
        public FakeGitHub Watcher { get; } = new();
        public FakeGitHub Doorbell { get; } = new();
        public Dictionary<string, FakeGitHub> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TriggerCycle Cycle { get; }

        public Setup(string yaml)
        {
            Watcher.Files[$"{WatcherRepo}:targets.yml"] = yaml;
            Cycle = new(Watcher, Doorbell, repo => Targets[repo], WatcherRepo, "targets.yml", new WorkFinder(() => Now),
                NullLogger.Instance, () => Now);
        }

        public string[] Dispatched => Doorbell.Dispatches.Select(d =>
        {
            Assert.Equal(WatcherRepo, d.Repo);
            Assert.Equal("watch.yml", d.Workflow);
            return Assert.Single(d.Inputs, i => i.Key == "target").Value;
        }).ToArray();
    }

    // TS-U5: one dispatch per target with work, none otherwise, and a failing target does not stop the others.
    [Fact]
    public async Task CycleDispatchesOncePerTargetWithWork()
    {
        var setup = new Setup("""
            targets:
              - repo: owner/new-head
              - repo: owner/two-reports
              - repo: owner/idle
              - repo: owner/broken
              - repo: owner/disabled
                enabled: false
            """);
        setup.Targets["owner/new-head"] = new() { CheckList = [Done(sha: "old")] };
        var twoReports = new FakeGitHub { CheckList = [Pending(runId: "41", id: 7), Pending(runId: "42", id: 8)] };
        twoReports.JobsByRun[41] = [Finished()];
        twoReports.JobsByRun[42] = [Finished("success")];
        setup.Targets["owner/two-reports"] = twoReports;
        setup.Targets["owner/idle"] = new() { CheckList = [Done()] };
        setup.Targets["owner/broken"] = new() { ChecksError = true };
        setup.Targets["owner/disabled"] = new();

        var result = await setup.Cycle.Run(Ct);

        Assert.Equal(["owner/new-head", "owner/two-reports"], setup.Dispatched);
        Assert.Equal(new CycleResult(4, 2, 1), result.Result);
    }

    // ADR-013 point 6: a report the Reporter owes is timed from its test job, and the check run is pending, never stale.
    [Fact]
    public async Task ACycleReportsWhatEachOwedReportHasBeenWaitingFor()
    {
        var setup = new Setup("targets:\n  - repo: owner/owed\n  - repo: owner/deleted\n  - repo: owner/idle");
        var owed = new FakeGitHub { CheckList = [Pending(runId: "41", id: 7)] };
        owed.JobsByRun[41] = [Finished(completedAt: Now.AddMinutes(-20))];
        setup.Targets["owner/owed"] = owed;
        var deleted = new FakeGitHub { CheckList = [Pending(runId: "42", id: 8)] };
        deleted.JobsByRun[42] = null;
        setup.Targets["owner/deleted"] = deleted;
        setup.Targets["owner/idle"] = new() { CheckList = [Done()] };

        var seen = (await setup.Cycle.Run(Ct)).Observations;

        Assert.Equal(["owner/owed", "owner/deleted", "owner/idle"], seen.Targets);
        Assert.Equal(["owner/owed", "owner/deleted", "owner/idle"], seen.Examined);
        Assert.Equal([new("owner/owed", 7, "check 7: the main-watcher job of target run 41 has completed", Now.AddMinutes(-20)),
            new PendingReport("owner/deleted", 8, "check 8: target run 42 was deleted", default)], seen.Pending);
        Assert.True(seen.WatchRunStarted);
        Assert.Null(seen.WatchRunCompleted);
    }

    // The newest completion's own time, not "a completed run was seen": the same run is read again on every cycle for two hours.
    [Fact]
    public async Task ACycleReportsWhenTheNewestWatchRunCompleted()
    {
        var setup = new Setup("targets:\n  - repo: owner/repo");
        setup.Targets["owner/repo"] = new() { CheckList = [Done()] };
        setup.Watcher.RunList = [
            new(1, "watch owner/other", Now.AddHours(-2), "completed", Now.AddMinutes(-100)),
            new(2, "watch owner/other", Now.AddHours(-1), "completed", Now.AddMinutes(-55))];
        var seen = (await setup.Cycle.Run(Ct)).Observations;
        Assert.Equal(Now.AddMinutes(-55), seen.WatchRunCompleted);
        Assert.False(seen.WatchRunStarted);

        // A run GitHub gave no update time for is dated by its creation: earlier than the truth, so it never hides a stall.
        setup.Watcher.RunList = [new(3, "watch owner/other", Now.AddMinutes(-30), "completed")];
        Assert.Equal(Now.AddMinutes(-30), (await setup.Cycle.Run(Ct)).Observations.WatchRunCompleted);

        // An unreadable run list says nothing either way, so the "no run completed" clock does not move.
        setup.Watcher.RunsError = true;
        Assert.Null((await setup.Cycle.Run(Ct)).Observations.WatchRunCompleted);

        // Nor does a window holding only unfinished runs.
        setup.Watcher.RunsError = false;
        setup.Watcher.RunList = [new(4, "watch owner/other", Now, "in_progress", Now)];
        Assert.Null((await setup.Cycle.Run(Ct)).Observations.WatchRunCompleted);
    }

    [Fact]
    public async Task TargetWithAQueuedOrRunningWatchCycleIsSkipped()
    {
        var setup = new Setup("targets:\n  - repo: owner/a\n  - repo: owner/b\n  - repo: owner/c");
        foreach (var repo in new[] { "owner/a", "owner/b", "owner/c" }) setup.Targets[repo] = new();
        setup.Watcher.RunList = [new(1, "watch OWNER/A", Now, "in_progress"), new(2, "watch owner/b", Now, "completed"), new(3, "watch owner/c", Now, "pending")];
        await setup.Cycle.Run(Ct);
        Assert.Equal(["owner/b"], setup.Dispatched);

        // Duplicates are harmless, so an unreadable run list skips nothing.
        setup.Watcher.RunsError = true;
        setup.Doorbell.Dispatches.Clear();
        await setup.Cycle.Run(Ct);
        Assert.Equal(["owner/a", "owner/b", "owner/c"], setup.Dispatched);
    }

    [Fact]
    public async Task IdleTargetsCauseNoDispatch()
    {
        var setup = new Setup("targets:\n  - repo: owner/repo");
        setup.Targets["owner/repo"] = new() { CheckList = [Done()] };
        for (var i = 0; i < 3; i++) await setup.Cycle.Run(Ct);
        Assert.Empty(setup.Doorbell.Dispatches);
    }

    [Theory]
    [InlineData("targets:\n  - repo: owner/repo\n    typo: true")]
    [InlineData("targets:\n  - repo: not-a-repo")]
    [InlineData("")]
    [InlineData(null)]
    public async Task InvalidTargetsFailFastAtStartupButKeepTheLastGoodListLater(string? invalid)
    {
        var setup = new Setup(invalid!);
        if (invalid is null) setup.Watcher.Files.Clear();
        await Assert.ThrowsAsync<WorkerConfigurationException>(() => setup.Cycle.Run(Ct));

        setup = new Setup("targets:\n  - repo: owner/repo");
        setup.Targets["owner/repo"] = new();
        await setup.Cycle.Run(Ct);
        if (invalid is null) setup.Watcher.Files.Clear();
        else setup.Watcher.Files[$"{WatcherRepo}:targets.yml"] = invalid;
        setup.Doorbell.Dispatches.Clear();
        await setup.Cycle.Run(Ct);
        Assert.Equal(["owner/repo"], setup.Dispatched);
    }

    [Fact]
    public void HealthIsLiveWhileCyclesKeepFinishing()
    {
        var health = new WorkerHealth(TimeSpan.FromSeconds(60), Now);
        Assert.Equal(TimeSpan.FromMinutes(7), health.Allowance);
        Assert.True(health.IsLive(Now.AddMinutes(7)));
        Assert.False(health.IsLive(Now.AddMinutes(7).AddSeconds(1)));
        health.Finished(Now.AddMinutes(6), new(1, 0, 1));
        Assert.True(health.IsLive(Now.AddMinutes(13)));
    }
}
