using MainWatcher.Scenarios.Scenarios;

namespace MainWatcher.Scenarios;

/// <summary>
/// Every unit of the suite. Together they cover TS-S1 to TS-S18 (TS-001 §6), which a release requires (§5); TS-S13 is also
/// run on its own when the reporter pin changes.
/// </summary>
public static class Catalogue
{
    public static List<Scenario> All() =>
    [
        // The watcher outage: these four share it, each on a target of its own (Sandbox.Outage).
        new WorkerDown(),
        new LeaseLapse(),
        new ClosedBeforeRecovery(),
        new SweepAfterRenewal(),
        // Long, started at once, and needing no worker while they wait for their run deadline.
        new RunDeadline(),
        new UnstoppableRun(),
        // The pool.
        new IdleHead(),
        new QuickPushes(),
        new Resolution(),
        new LockedQueue(),
        new HandMadeIssue(),
        new CredentialScope(),
        new GateApiDown(),
        new TeamNotify(),
        new CancelledRun(),
        new Timings(),
        new ReporterReplay(),
        new ReportingPending(),
        new BrokenRuns(),
        new LateFailures(),
        new HungTests(),
        new StuckReportJob(),
        new QueueDeadline(),
        new QueuedBeforeLock(),
        new NeutralRetries()
    ];
}
