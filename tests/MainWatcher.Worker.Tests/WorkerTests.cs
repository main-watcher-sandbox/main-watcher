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
        var work = await new WorkFinder(() => c.Now).Work(new() { Repo = "owner/repo", PollInterval = (int)c.PollInterval.TotalMinutes }, target, Ct);
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
        var work = await new WorkFinder(() => Now).Work(Watched(), target, Ct);
        Assert.Contains("main-watcher job of target run 41 has completed", work!.Reason);
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("queued")]
    public async Task RunningMainWatcherJobIsNotWorkEvenWhenOtherJobsFinished(string status)
    {
        var target = new FakeGitHub { CheckList = [Pending()] };
        target.JobsByRun[41] = [Job("tests / report", "completed"), Job("tests / main-watcher", status)];
        Assert.Null(await new WorkFinder(() => Now).Work(Watched(), target, Ct));
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
        var work = await new WorkFinder(() => Now).Work(Watched(), target, Ct);
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
        var work = await new WorkFinder(() => Now).Work(Watched(), target, Ct);
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
        var stopping = await new WorkFinder(() => Now).Work(Watched(), target, Ct);
        Assert.Contains("was asked to stop and has not stopped", stopping!.Reason);
        Assert.Equal(Now.AddMinutes(-5), stopping.Since);

        // Once it has stopped, it is a report the Reporter owes, not a stale run: the outcome table judges its steps.
        target.JobsByRun[41] = [Finished(completedAt: Now.AddMinutes(-1))];
        var owed = await new WorkFinder(() => Now).Work(Watched(), target, Ct);
        Assert.Contains("has completed", owed!.Reason);
        Assert.Equal(7, owed.Check);
    }

    [Fact]
    public async Task TheSandboxQueueDeadlineIsSharedWithThePlanner()
    {
        var target = new FakeGitHub { CheckList = [Pending(age: 10)] };
        target.JobsByRun[41] = [Job("tests / main-watcher", "queued")];
        Assert.Null(await new WorkFinder(() => Now).Work(Watched(), target, Ct));
        Assert.NotNull(await new WorkFinder(() => Now, TimeSpan.FromMinutes(10)).Work(Watched(), target, Ct));
    }

    [Fact]
    public async Task DeletedRunsAndRunsWithoutTheJobAreWorkForTheReporter()
    {
        var target = new FakeGitHub { CheckList = [Pending()] };
        target.JobsByRun[41] = null;
        Assert.Contains("was deleted", (await new WorkFinder(() => Now).Work(Watched(), target, Ct))!.Reason);
        // A completed run with no main-watcher job: the Reporter records the broken contract.
        target.JobsByRun[41] = [];
        Assert.NotNull(await new WorkFinder(() => Now).Work(Watched(), target, Ct));
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
        Assert.Equal(expected, await new WorkFinder(() => Now).Work(Watched(), target, Ct) is not null);
        Assert.Equal(["runs:owner/repo:main-watcher-tests.yml"], target.Reads);
    }

    [Fact]
    public async Task NothingIsWorkForATestedIdleHead()
    {
        var target = new FakeGitHub { CheckList = [Done("failure"), Done("success", "old")] };
        Assert.Null(await new WorkFinder(() => Now).Work(Watched(), target, Ct));
    }

    static Issue Lock(int number, DateTimeOffset? until, string author = "main-watcher[bot]", string type = "Bot") =>
        new(number, "main is broken", until is null ? "Locked." : Markers.Set("Locked.", (Lease.Until, Markers.Stamp(until.Value))),
            author, type, $"https://github.com/owner/repo/issues/{number}");

    // TS-U5 (d): a lock is enforced only while the watcher renews its lease, so a lease an hour old is work (ADR-014).
    [Theory]
    [InlineData(181, false)]
    [InlineData(180, true)]
    public async Task AnOpenLockWhoseLeaseIsAnHourOldIsWork(int until, bool expected)
    {
        // A head with a result of its own: the lease is then the only thing left that could need a cycle.
        var target = new FakeGitHub { CheckList = [Done()] };
        target.IssueList.Add(Lock(1, Now.AddMinutes(until)));
        var work = await new WorkFinder(() => Now).Work(Watched(), target, Ct);
        Assert.Equal(expected, work is not null);
        if (!expected) return;
        Assert.Contains("lock #1: its lease has wanted renewing since 2026-09-16T19:00:00Z", work!.Reason);
        // Dated by the moment renewal became due, and not a report owed: only the Reporter's own work carries a check ID.
        Assert.Equal(Now, work.Since);
        Assert.Null(work.Check);
    }

    [Fact]
    public async Task TheSandboxLockLeaseIsSharedWithThePlanner()
    {
        var target = new FakeGitHub { CheckList = [Done()] };
        target.IssueList.Add(Lock(1, Now.AddMinutes(6)));
        var short10 = new Target { Repo = "owner/repo", PollInterval = 15, LockLease = TimeSpan.FromMinutes(10) };
        // Half of a ten-minute lease, so the renewal is asked for while the gate is still enforcing the lock, not after.
        Assert.Null(await new WorkFinder(() => Now).Work(short10, target, Ct));
        target.IssueList[0] = Lock(1, Now.AddMinutes(5));
        Assert.NotNull(await new WorkFinder(() => Now).Work(short10, target, Ct));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(241, true)]
    public async Task AnUnreadableOrImpossibleLeaseIsWorkAndDatesNothing(int? until, bool expected)
    {
        var target = new FakeGitHub { CheckList = [Done()] };
        target.IssueList.Add(Lock(1, until is null ? null : Now.AddMinutes(until.Value)));
        var work = await new WorkFinder(() => Now).Work(Watched(), target, Ct);
        Assert.Equal(expected, work is not null);
        Assert.Null(work!.Since);
    }

    [Fact]
    public async Task OnlyTheAppsOwnLocksCarryALeaseToRenew()
    {
        var target = new FakeGitHub { CheckList = [Done()] };
        target.IssueList.Add(Lock(1, Now.AddMinutes(-1), "alice", "User"));
        target.IssueList.Add(Lock(2, Now.AddMinutes(-1), "other-app[bot]"));
        Assert.Null(await new WorkFinder(() => Now).Work(Watched(), target, Ct));
        // A renamed App is still the App: MW_BOT_LOGIN names it, as watch.yml and the gate do.
        Assert.NotNull(await new WorkFinder(() => Now, botLogin: "other-app[bot]").Work(Watched(), target, Ct));
    }

    static Issue ClosedLock(int number, DateTimeOffset closed, string? body = "Locked.", string author = "main-watcher[bot]") =>
        new(number, "main is broken", body, author, "Bot", $"https://github.com/owner/repo/issues/{number}",
            "closed", "completed", closed, Id: number, CreatedAt: closed.AddHours(-1), ClosedAt: closed);

    // TS-U5 (e): a lock that has closed still owes reports for the merges made during it, and nothing else asks for a cycle
    // once it is closed (ADR-015 point 6).
    [Fact]
    public async Task AClosedLockNotYetReconciledIsWork()
    {
        var target = new FakeGitHub { CheckList = [Done()] };
        target.IssueList.Add(ClosedLock(1, Now.AddMinutes(-30)));
        var work = await new WorkFinder(() => Now).Work(Watched(), target, Ct);
        Assert.Equal("lock #1: it closed without being reconciled", work!.Reason);
        // Dated by the closure, so the sweep can say how long the reports have been owed.
        Assert.Equal(Now.AddMinutes(-30), work.Since);
        Assert.Null(work.Check);
    }

    [Fact]
    public async Task AReconciledOrForeignOrRecentlyOpenLockIsNotWork()
    {
        var target = new FakeGitHub { CheckList = [Done()] };
        // Complete: its whole window has been checked.
        target.IssueList.Add(ClosedLock(1, Now.AddMinutes(-30),
            Markers.Set("Locked.", (Reconciliation.Complete, Reconciliation.CompleteValue))));
        // Not the App's lock, so not the App's window.
        target.IssueList.Add(ClosedLock(2, Now.AddMinutes(-30), "Locked.", "other-app[bot]"));
        // Closed longer ago than reconcile_lookback: not revisited (R-21). The Issues read bounds this by its own window.
        target.IssueList.Add(ClosedLock(3, Now - Reporter.ReconcileLookback - TimeSpan.FromDays(1)));
        Assert.Null(await new WorkFinder(() => Now).Work(Watched(), target, Ct));
    }

    // TS-U12 (ADR-016): while a lock owes a queue sweep, the worker keeps asking for cycles, and times the debt so that the
    // "queue sweep unfinished" alert can be raised.
    [Fact]
    public async Task AnOpenLockOwingAQueueSweepIsWork()
    {
        var target = new FakeGitHub { CheckList = [Done()] };
        // A lease renewed 40 minutes ago, so nothing but the sweep is owed.
        var owed = Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddMinutes(200))),
            (QueueSweep.Required, Markers.Stamp(Now.AddMinutes(-20))));
        target.IssueList.Add(Lock(1, null) with { Body = owed });
        var work = await new WorkFinder(() => Now).Work(Watched(), target, Ct);
        Assert.Equal("lock #1: its queue sweep for 2026-09-16T18:40:00Z is unfinished", work!.Reason);
        // Dated by the generation it owes, and named by the lock, which is how the alert times and titles it.
        Assert.Equal(Now.AddMinutes(-20), work.Since);
        Assert.Equal(1, work.Sweep);
        Assert.Null(work.Check);

        // Once the sweep has caught up with that generation, the lock is no longer work.
        target.IssueList[0] = target.IssueList[0] with
        {
            Body = Markers.Set(owed, (QueueSweep.Swept, Markers.Stamp(Now.AddMinutes(-20))))
        };
        Assert.Null(await new WorkFinder(() => Now).Work(Watched(), target, Ct));
    }

    // PR #56 review: only one reason can be the reason, and a report owed is found first. The sweep debt must be seen all the
    // same, or a Reporter that keeps failing would hide it for as long as it lasted — exactly when groups queued before the
    // lock are still free to merge.
    [Fact]
    public async Task ASweepOwedIsSeenEvenWhenAnotherReasonWinsTheCycle()
    {
        var target = new FakeGitHub { CheckList = [Pending(runId: "41", id: 7)] };
        target.JobsByRun[41] = [Finished(completedAt: Now.AddMinutes(-20))];
        target.IssueList.Add(Lock(1, Now.AddMinutes(200)) with
        {
            Body = Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddMinutes(200))),
                (QueueSweep.Required, Markers.Stamp(Now.AddMinutes(-30))))
        });

        var found = await new WorkFinder(() => Now).Find(Watched(), target, Ct);

        // The report is what the cycle is for, and the sweep is still reported, dated by its own generation.
        Assert.Contains("the main-watcher job of target run 41 has completed", found.Work!.Reason);
        Assert.Equal(7, found.Work.Check);
        Assert.Equal("lock #1: its queue sweep for 2026-09-16T18:30:00Z is unfinished", found.Sweep!.Reason);
        Assert.Equal(Now.AddMinutes(-30), found.Sweep.Since);
        Assert.Equal(1, found.Sweep.Sweep);

        // Saying nothing is what says the sweep has finished, so it is only ever silent once a cycle has looked and found so.
        target.IssueList[0] = target.IssueList[0] with
        {
            Body = Markers.Set(target.IssueList[0].Body, (QueueSweep.Swept, Markers.Stamp(Now.AddMinutes(-30))))
        };
        Assert.Null((await new WorkFinder(() => Now).Find(Watched(), target, Ct)).Sweep);
    }

    // A closed lock enforces nothing, so re-running a gate for it would block nothing: its sweep debt asks for no cycle.
    [Fact]
    public async Task AClosedLockOwesNoQueueSweep()
    {
        var target = new FakeGitHub { CheckList = [Done()] };
        target.IssueList.Add(ClosedLock(1, Now.AddMinutes(-30),
            Markers.Set("Locked.", (QueueSweep.Required, Markers.Stamp(Now.AddMinutes(-20))),
                (Reconciliation.Complete, Reconciliation.CompleteValue))));
        Assert.Null(await new WorkFinder(() => Now).Work(Watched(), target, Ct));
    }

    [Fact]
    public async Task WorkFoundEarlierWinsOverAnOldLease()
    {
        var target = new FakeGitHub { CheckList = [Pending()] };
        target.JobsByRun[41] = [Finished()];
        target.IssueList.Add(Lock(1, Now.AddMinutes(-1)));
        Assert.Contains("has completed", (await new WorkFinder(() => Now).Work(Watched(), target, Ct))!.Reason);
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

    // PR #56 review: the cycle must hand both debts to the alerts, not only the one it dispatched for. Nothing else produces
    // this combination in production, which is why it is tested here and not only against WorkerAlerts.
    [Fact]
    public async Task ACycleReportsASweepOwedBesideTheReportItDispatchedFor()
    {
        var setup = new Setup("targets:\n  - repo: owner/stuck\n  - repo: owner/idle");
        var stuck = new FakeGitHub { CheckList = [Pending(runId: "41", id: 7)] };
        stuck.JobsByRun[41] = [Finished(completedAt: Now.AddMinutes(-20))];
        stuck.IssueList.Add(new(1, "main is broken",
            Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddMinutes(200))),
                (QueueSweep.Required, Markers.Stamp(Now.AddMinutes(-30)))),
            "main-watcher[bot]", "Bot", "https://github.com/owner/stuck/issues/1"));
        setup.Targets["owner/stuck"] = stuck;
        // An idle target with a lock that owes nothing: it is examined, so its sweep clock would be cleared, not started.
        var idle = new FakeGitHub { CheckList = [Done()] };
        idle.IssueList.Add(new(2, "main is broken", Markers.Set("Locked.", (Lease.Until, Markers.Stamp(Now.AddMinutes(200)))),
            "main-watcher[bot]", "Bot", "https://github.com/owner/idle/issues/2"));
        setup.Targets["owner/idle"] = idle;

        var seen = (await setup.Cycle.Run(Ct)).Observations;

        Assert.Equal(["owner/stuck"], setup.Dispatched);
        Assert.Equal([new PendingReport("owner/stuck", 7, "check 7: the main-watcher job of target run 41 has completed",
            Now.AddMinutes(-20))], seen.Pending);
        Assert.Equal([new PendingSweep("owner/stuck", 1, "lock #1: its queue sweep for 2026-09-16T18:30:00Z is unfinished",
            Now.AddMinutes(-30))], seen.Sweeps);
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

static class WorkFinderTestExtensions
{
    /// <summary>
    /// The one reason a cycle would be dispatched for, which is what most of these cases are about. What the target owes
    /// besides that reason is the subject of its own tests.
    /// </summary>
    public static async Task<Work?> Work(this WorkFinder finder, Target target, IGitHubGateway github, CancellationToken ct) =>
        (await finder.Find(target, github, ct)).Work;
}
