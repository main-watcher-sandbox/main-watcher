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
    static WorkflowJob Finished(string conclusion = "failure") => new("tests / main-watcher", "completed",
        [new("main-watcher-test", conclusion), new("main-watcher-tests-finished", "success")]);

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
        Assert.Contains("main-watcher job of target run 41 has completed", await new WorkFinder(() => Now).Find(Watched(), target, Ct));
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("queued")]
    public async Task RunningMainWatcherJobIsNotWorkEvenWhenOtherJobsFinished(string status)
    {
        var target = new FakeGitHub { CheckList = [Pending(age: 200)] };
        target.JobsByRun[41] = [Job("tests / report", "completed"), Job("tests / main-watcher", status)];
        Assert.Null(await new WorkFinder(() => Now).Find(Watched(), target, Ct));
    }

    [Fact]
    public async Task DeletedRunsAndRunsWithoutTheJobAreWorkForTheReporter()
    {
        var target = new FakeGitHub { CheckList = [Pending()] };
        target.JobsByRun[41] = null;
        Assert.Contains("was deleted", await new WorkFinder(() => Now).Find(Watched(), target, Ct));
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
        Assert.Equal(new CycleResult(4, 2, 1), result);
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
