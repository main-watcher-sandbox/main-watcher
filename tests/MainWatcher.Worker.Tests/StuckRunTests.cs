using MainWatcher.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace MainWatcher.Worker.Tests;

/// <summary>TS-U17: a <c>watch.yml</c> run that has not started is stopped, and alerted about, unless a person can approve it (ADR-020).</summary>
public class StuckRunTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    const string WatcherRepo = "owner/watcher";
    const string Target = "owner/repo";
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static WorkflowRun Run(string status, int age, long id = 9, string title = $"watch {Target}") => new(id, title, Now.AddMinutes(-age), status, Now);
    static PendingDeployment Gate(int waitTimer = 0, params string[] reviewers) => new("reporter", TimeSpan.FromMinutes(waitTimer), reviewers);

    sealed class Setup
    {
        public FakeGitHub Watcher { get; } = new();
        public FakeGitHub Doorbell { get; } = new();
        /// <summary>A target whose head is untested, so it has work whenever its own cycle is not in the way.</summary>
        public FakeGitHub Repo { get; } = new();
        public DateTimeOffset At { get; set; } = Now;
        public TriggerCycle Cycle { get; }

        public Setup(params WorkflowRun[] runs)
        {
            Watcher.Files[$"{WatcherRepo}:targets.yml"] = $"targets:\n  - repo: {Target}";
            Watcher.RunList = [.. runs];
            Cycle = new(Watcher, Doorbell, _ => Repo, WatcherRepo, "targets.yml", new WorkFinder(() => At), NullLogger.Instance, () => At);
        }

        public async Task<CycleObservations> Run() => (await Cycle.Run(Ct)).Observations;
    }

    // Anything but in_progress and completed has not started, and is stopped once 20 minutes old.
    [Theory]
    [InlineData("waiting")]
    [InlineData("queued")]
    [InlineData("pending")]
    [InlineData("requested")]
    public async Task AnUnstartedRunIsCancelledAtTwentyMinutes(string status)
    {
        var young = new Setup(Run(status, 19));
        Assert.Empty((await young.Run()).StuckRuns!);
        Assert.Empty(young.Doorbell.Cancels);
        // Nothing is asked about a run's gates before it is due.
        Assert.DoesNotContain(young.Watcher.Reads, r => r.StartsWith("gates:", StringComparison.Ordinal));

        var due = new Setup(Run(status, 20));
        var seen = await due.Run();
        Assert.Equal(["cancel:9"], due.Doorbell.Cancels);
        var stuck = Assert.Single(seen.StuckRuns!);
        Assert.Equal((Target, StuckStage.Cancel, status, $"https://github.com/{WatcherRepo}/actions/runs/9"),
            (stuck.Repo, stuck.Stage, stuck.State, stuck.Url));
        // It still marks its target active, so no second cycle is dispatched beside it.
        Assert.Empty(due.Doorbell.Dispatches);
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("completed")]
    public async Task AStartedOrFinishedRunIsNeverStopped(string status)
    {
        var setup = new Setup(Run(status, 300));
        Assert.Empty((await setup.Run()).StuckRuns!);
        Assert.Empty(setup.Doorbell.Cancels);
    }

    // A sweep names no target, and is out of scope (ADR-020 point 6).
    [Fact]
    public async Task ASweepRunIsLeftAlone()
    {
        var setup = new Setup(Run("waiting", 60, title: "sweep"));
        Assert.Empty((await setup.Run()).StuckRuns!);
        Assert.Empty(setup.Doorbell.Cancels);
        Assert.DoesNotContain(setup.Watcher.Reads, r => r.StartsWith("gates:", StringComparison.Ordinal));
    }

    // ADR-013 point 5's escalation, timed from the deadline alone, so a restart at any point resumes where it was.
    [Theory]
    [InlineData(34, "cancel:9", StuckStage.Cancel)]
    [InlineData(35, "force-cancel:9", StuckStage.ForceCancel)]
    [InlineData(49, "force-cancel:9", StuckStage.ForceCancel)]
    [InlineData(50, "force-cancel:9", StuckStage.Unstoppable)]
    [InlineData(110, "force-cancel:9", StuckStage.Unstoppable)]
    public async Task TheStopEscalatesFromTheDeadline(int age, string request, StuckStage stage)
    {
        var setup = new Setup(Run("queued", age));
        var seen = await setup.Run();
        Assert.Equal([request], setup.Doorbell.Cancels);
        Assert.Equal(stage, Assert.Single(seen.StuckRuns!).Stage);
    }

    // The reporter environment must have no reviewers, but a run a person could approve is not cancelled: it is alerted about.
    [Fact]
    public async Task ARunWaitingForAListedReviewerIsOnlyReported()
    {
        var setup = new Setup(Run("waiting", 60));
        setup.Watcher.GatesByRun[9] = [Gate(0, "octocat", "team platform")];
        var stuck = Assert.Single((await setup.Run()).StuckRuns!);
        Assert.Equal(StuckStage.Reviewers, stuck.Stage);
        Assert.Equal(["octocat", "team platform"], stuck.Reviewers);
        Assert.Empty(setup.Doorbell.Cancels);
    }

    // Run 11's shape (#67): held at a gate that lists nobody, so nobody can ever approve it.
    [Fact]
    public async Task ARunWaitingAtAGateNobodyCanApproveIsCancelled()
    {
        var setup = new Setup(Run("waiting", 25));
        setup.Watcher.GatesByRun[9] = [Gate()];
        await setup.Run();
        Assert.Equal(["cancel:9"], setup.Doorbell.Cancels);
        Assert.Contains("gates:9", setup.Watcher.Reads);
    }

    // A wait timer is a legitimate hold, so it moves the deadline.
    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    public async Task AWaitTimerIsAddedToTheDeadline(int age, bool cancelled)
    {
        var setup = new Setup(Run("waiting", age));
        setup.Watcher.GatesByRun[9] = [Gate(10)];
        await setup.Run();
        Assert.Equal(cancelled, setup.Doorbell.Cancels.Count > 0);
    }

    // Unread, a gate might list a reviewer, so nothing is cancelled; the failure counts towards the failing-cycles alert.
    [Fact]
    public async Task UnreadableGatesCancelNothing()
    {
        var setup = new Setup(Run("waiting", 60));
        setup.Watcher.GatesError = true;
        var seen = await setup.Run();
        Assert.Empty(setup.Doorbell.Cancels);
        Assert.Empty(seen.StuckRuns!);
        Assert.Contains(seen.Problems, p => p.Contains("could not read the gates of watch.yml run 9", StringComparison.Ordinal));
    }

    // A refused cancel changes nothing: the next cycle asks again, and the escalation goes on from the deadline.
    [Fact]
    public async Task ARefusedCancelIsAskedAgainNextCycle()
    {
        var setup = new Setup(Run("queued", 25));
        setup.Doorbell.CancelRefusal = "HTTP 409";
        await setup.Run();
        await setup.Run();
        Assert.Equal(["cancel:9", "cancel:9"], setup.Doorbell.Cancels);
    }

    // Once the stuck run has stopped, it no longer marks its target active, and the next cycle dispatches again.
    [Fact]
    public async Task OnceTheRunHasStoppedTheTargetIsDispatchedAgain()
    {
        var setup = new Setup(Run("waiting", 25));
        await setup.Run();
        Assert.Empty(setup.Doorbell.Dispatches);

        setup.Watcher.RunList = [Run("completed", 25)];
        var seen = await setup.Run();
        Assert.Equal(Target, Assert.Single(setup.Doorbell.Dispatches).Inputs["target"]);
        Assert.Empty(seen.StuckRuns!);
    }

    // PR #72 review: a run older than the two-hour window is still seen. A two-hour wait timer puts the deadline at 140
    // minutes, and a run that cannot be stopped is force-cancelled on every cycle until it stops.
    [Theory]
    [InlineData(139, 120, null)]
    [InlineData(140, 120, "cancel:9")]
    [InlineData(300, 0, "force-cancel:9")]
    public async Task ARunOlderThanTheWindowIsStillJudged(int age, int waitTimer, string? request)
    {
        var setup = new Setup(Run("waiting", age));
        setup.Watcher.GatesByRun[9] = [Gate(waitTimer)];
        var seen = await setup.Run();
        Assert.Equal(request is null ? [] : [request], setup.Doorbell.Cancels);
        // It still marks its target active: it holds the target's concurrency group until it stops.
        Assert.Empty(setup.Doorbell.Dispatches);
        if (request is not null) Assert.Single(seen.StuckRuns!);
        Assert.Contains("runs-in:waiting,queued,pending,requested", setup.Watcher.Reads);
    }

    // PR #72 review: a target whose gates went unread is named as unjudged, so its alert keeps its state.
    [Fact]
    public async Task UnreadableGatesLeaveTheTargetUnjudged()
    {
        var setup = new Setup(Run("waiting", 60));
        setup.Watcher.GatesError = true;
        Assert.Equal([Target], (await setup.Run()).StuckUnjudged);
    }

    // An unreadable run list says nothing about stuck runs, so their alerts are neither raised nor cleared.
    [Fact]
    public async Task AnUnreadableRunListSaysNothingAboutStuckRuns()
    {
        var setup = new Setup(Run("waiting", 25));
        setup.Watcher.RunsError = true;
        Assert.Null((await setup.Run()).StuckRuns);
        Assert.Empty(setup.Doorbell.Cancels);
    }

    sealed class Alerting
    {
        public FakeGitHub Doorbell { get; } = new();
        public DateTimeOffset At { get; set; } = Now;
        public WorkerAlerts Alerts { get; }
        public Alerting() => Alerts = new(new Alerts(Doorbell, WatcherRepo), new NoFailures(), NullLogger.Instance, Now, () => At);
        public Task Cycle(IReadOnlyList<StuckRun>? stuck, params string[] unjudged) =>
            Alerts.Review(new() { StuckRuns = stuck, StuckUnjudged = unjudged }, null, Ct);
        public string[] Titles => Doorbell.IssueList.Select(i => i.Title).ToArray();
        public string Body(string title) => Doorbell.IssueList.Single(i => i.Title == title).Body ?? "";
        public int Comments(string title) => Doorbell.CommentsByIssue.GetValueOrDefault(Doorbell.IssueList.Single(i => i.Title == title).Number, []).Count;
    }

    sealed class NoFailures : IAccessHealth
    {
        public IReadOnlyDictionary<string, string> TokenFailures { get; } = new Dictionary<string, string>();
        public RateLimit? TakeLowestRateLimit() => null;
    }

    static StuckRun Stuck(StuckStage stage, string repo = Target, int age = 25, params string[] reviewers) =>
        new(repo, 9, $"https://github.com/{WatcherRepo}/actions/runs/9", "waiting", Now.AddMinutes(-age), Now.AddMinutes(20 - age), stage, reviewers);

    // The condition is named in an alert rather than showing up only as a pending report.
    [Fact]
    public async Task AStuckRunIsNamedInItsOwnAlert()
    {
        var setup = new Alerting();
        await setup.Cycle([Stuck(StuckStage.Cancel)]);
        Assert.Equal(["`watch.yml` run stuck `waiting` on owner/repo"], setup.Titles);
        var body = setup.Body("`watch.yml` run stuck `waiting` on owner/repo");
        Assert.Contains("has been `waiting` since 2026-09-23 15:35 UTC (25 minutes)", body);
        Assert.Contains("the worker cancelled it", body);
        Assert.Contains("https://github.com/owner/watcher/actions/runs/9", body);

        // Still stuck: repeated at most hourly, as a comment on the same issue.
        setup.At = Now.AddMinutes(30);
        await setup.Cycle([Stuck(StuckStage.Unstoppable)]);
        Assert.Equal(["`watch.yml` run stuck `waiting` on owner/repo", "`watch.yml` run could not be stopped on owner/repo"], setup.Titles);
        Assert.Equal(0, setup.Comments("`watch.yml` run stuck `waiting` on owner/repo"));
        Assert.Contains("35 minutes after it was first cancelled", setup.Body("`watch.yml` run could not be stopped on owner/repo"));

        // Once it has stopped, the conditions clear, so a later stuck run alerts at once.
        setup.At = Now.AddMinutes(31);
        await setup.Cycle([]);
        setup.At = Now.AddMinutes(32);
        await setup.Cycle([Stuck(StuckStage.Cancel)]);
        Assert.Equal(1, setup.Comments("`watch.yml` run stuck `waiting` on owner/repo"));
    }

    [Fact]
    public async Task ARunHeldForAReviewerNamesThemAndTheRequirement()
    {
        var setup = new Alerting();
        await setup.Cycle([Stuck(StuckStage.Reviewers, reviewers: ["octocat", "team platform"])]);
        var title = Assert.Single(setup.Titles);
        Assert.Equal("`watch.yml` run waiting for a reviewer on owner/repo", title);
        Assert.Contains("waiting for `octocat`, `team platform` to approve it", setup.Body(title));
        Assert.Contains("must have no required reviewers", setup.Body(title));
    }

    [Fact]
    public async Task EachTargetGetsItsOwnAlertAndAnUnreadListClearsNothing()
    {
        var setup = new Alerting();
        await setup.Cycle([Stuck(StuckStage.Cancel, "owner/a"), Stuck(StuckStage.Cancel, "owner/b")]);
        Assert.Equal(["`watch.yml` run stuck `waiting` on owner/a", "`watch.yml` run stuck `waiting` on owner/b"], setup.Titles);

        // A cycle that could not read the run list says nothing, so the clock is not reset; an hour later it repeats.
        setup.At = Now.AddMinutes(30);
        await setup.Cycle(null);
        setup.At = Now.AddHours(1);
        await setup.Cycle([Stuck(StuckStage.ForceCancel, "owner/a")]);
        Assert.Equal(1, setup.Comments("`watch.yml` run stuck `waiting` on owner/a"));
        Assert.Equal(0, setup.Comments("`watch.yml` run stuck `waiting` on owner/b"));
    }

    // PR #72 review: a cycle that could not read a target's gates neither clears its alert nor restarts the hour, so the
    // next successful read two minutes later is not a second alert.
    [Fact]
    public async Task AnUnreadGateKeepsTheAlertsHourlyLimit()
    {
        var setup = new Alerting();
        await setup.Cycle([Stuck(StuckStage.Cancel)]);
        setup.At = Now.AddMinutes(1);
        await setup.Cycle([], Target);
        setup.At = Now.AddMinutes(2);
        await setup.Cycle([Stuck(StuckStage.Cancel)]);
        Assert.Equal(["`watch.yml` run stuck `waiting` on owner/repo"], setup.Titles);
        Assert.Equal(0, setup.Comments("`watch.yml` run stuck `waiting` on owner/repo"));

        // Another target's unread gates do not hold this one's alert: once its run is gone, it clears.
        setup.At = Now.AddMinutes(3);
        await setup.Cycle([], "owner/other");
        setup.At = Now.AddMinutes(4);
        await setup.Cycle([Stuck(StuckStage.Cancel)]);
        Assert.Equal(1, setup.Comments("`watch.yml` run stuck `waiting` on owner/repo"));
    }
}
