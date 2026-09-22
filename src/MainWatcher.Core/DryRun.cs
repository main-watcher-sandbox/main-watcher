namespace MainWatcher.Core;

/// <summary>One check the onboarding dry run made, in the order it made it.</summary>
public sealed record DryRunStep(string Name, bool Passed, string Detail);

/// <summary>
/// What the dry run found. It stops at the first failed check, because each one is what the next needs, so a report that did
/// not pass ends with the one to read.
/// </summary>
public sealed record DryRunReport(IReadOnlyList<DryRunStep> Steps)
{
    public bool Passed => Steps.Count > 0 && Steps.All(s => s.Passed);
}

/// <summary>
/// The onboarding dry run (FR-1, ARCH-001 §10 step 5): it exercises a target's whole test path before the gate becomes a
/// required check, so that bad setup is found by the person onboarding the repository rather than by the first real cycle.
/// <para>
/// It writes nothing to the target but the one <c>workflow_dispatch</c> it needs. In particular it creates no check run, so
/// nothing the trigger worker or the Reporter looks at exists, and a red suite cannot open a lock. That is the whole reason it
/// starts a test by hand instead of running a cycle: a cycle's check run is what turns a red result into a locked
/// <c>main</c>, which is exactly what a repository still being onboarded must not get.
/// </para>
/// </summary>
public sealed class DryRun(IGitHubGateway github, Func<DateTimeOffset>? clock = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null, Action<string>? log = null)
{
    /// <summary>How long the dispatched run has to appear in the target's run list.</summary>
    public static readonly TimeSpan DispatchWindow = TimeSpan.FromMinutes(10);
    /// <summary>
    /// What the <c>main-watcher</c> job gets over the target's own <c>timeout</c>: the reusable workflow's 20-minute margin
    /// for setup and upload (ADR-013), and 10 minutes more for the queue.
    /// </summary>
    public static readonly TimeSpan RunMargin = TimeSpan.FromMinutes(30);
    /// <summary>How often GitHub is asked again while waiting. A dry run is rare and manual, but it shares R-13's budget.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);
    /// <summary>What the gate workflow must run, whatever else the target's copy says.</summary>
    public const string GateAction = "/.github/actions/gate@";

    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;

    public async Task<DryRunReport> Run(Target target, CancellationToken ct)
    {
        var repo = target.Repo;
        var head = "";
        var runId = 0L;
        var callerPath = $".github/workflows/{GitHubGateway.Workflow}";
        var gatePath = $".github/workflows/{GitHubGateway.GateWorkflow}";
        var allowed = TimeSpan.FromMinutes(target.Timeout) + RunMargin;

        // The entry, then the two files it names, then one real test. Each check is what the next one needs, so the first
        // failure ends the run: there is nothing useful to say about a test that was never dispatched.
        (string Name, Func<CancellationToken, Task<(bool Passed, string Detail)>> Check)[] checks =
        [
            ("Target entry", _ => Task.FromResult((true, $"`{repo}`: `test_command` `{target.TestCommand}`, `results_glob` "
                + $"`{target.ResultsGlob}`, `timeout` {target.Timeout} min, `poll_interval` {target.PollInterval} min, "
                + $"`notify` [{string.Join(", ", target.Notify)}], `enabled` {(target.Enabled ? "true" : "false")}."))),

            // The check the Planner makes before every dispatch (ADR-009): a target that fails it is never tested at all.
            ("Test caller", async token =>
            {
                await github.ValidateTarget(target, token);
                return (true, $"`{callerPath}` calls the reusable workflow, with the entry's three execution settings.");
            }),

            // Whether the gate is a *required* check lives in the repository's ruleset, which needs administration to read and
            // the watcher never has. TS-S5 is what confirms that, which is why the guide runs it once at onboarding.
            ("Gate workflow", async token => await github.File(repo, gatePath, token) switch
            {
                null => (false, $"`{gatePath}` is missing on `main`. Copy `templates/main-watcher-gate.yml`."),
                "" => (false, $"`{gatePath}` is empty, or too large to read."),
                var gate when !gate.Contains(GateAction, StringComparison.Ordinal) =>
                    (false, $"`{gatePath}` does not run `{GateAction}`. Update it from `templates/main-watcher-gate.yml`."),
                _ => (true, $"`{gatePath}` runs `{GateAction}`. Whether it is a **required** merge-queue check cannot be read "
                    + "from here; TS-S5 confirms that.")
            }),

            ("Test run", async token =>
            {
                head = await github.MainHead(repo, token);
                // Two seconds of slack, as the Planner's own lookup allows, for clocks that disagree about when this happened.
                var since = now() - TimeSpan.FromSeconds(2);
                await github.DispatchWorkflow(repo, GitHubGateway.Workflow,
                    new Dictionary<string, string> { ["sha"] = head, ["check_run_id"] = "" }, token);
                // No run ID comes back: return_run_details belongs to the Planner's dispatch, which has a check run to record
                // it in. The caller's run-name carries the tested commit, so the run is found as recovery finds one (R-14).
                var deadline = now() + DispatchWindow;
                while (true)
                {
                    var matches = (await github.Runs(repo, GitHubGateway.Workflow, since, token))
                        .Where(r => r.Title == Planner.RunName(head) && r.CreatedAt >= since).ToArray();
                    if (matches.Length == 1)
                    {
                        runId = matches[0].Id;
                        return (true, $"`{GitHubGateway.Workflow}` dispatched for `{head}`: run {runId}.");
                    }
                    if (matches.Length > 1)
                        return (false, $"{matches.Length} runs of `{GitHubGateway.Workflow}` name `{head}`. Let the other runs "
                            + "of that commit finish, then dry-run again.");
                    if (now() >= deadline)
                        return (false, $"No run of `{GitHubGateway.Workflow}` named `{head}` appeared within "
                            + $"{DispatchWindow.TotalMinutes:0} minutes. Check that the caller is on `main`, that its "
                            + $"`run-name` is the template's, and that Actions is enabled in `{repo}`.");
                    await wait(Poll, token);
                }
            }),

            // The ADR-013 outcome table, read from the steps the Reporter reads, as soon as the main-watcher job has completed:
            // the separate report job is not part of the contract and is not waited for.
            ("Test outcome", async token =>
            {
                var deadline = now() + allowed;
                while (true)
                {
                    var jobs = await github.Jobs(repo, runId, token);
                    // In the seconds after a dispatch GitHub can answer with no jobs at all. That is a run whose job has not
                    // been created yet, not a broken contract, so it is waited out rather than judged. A null list is a
                    // deleted run, which is judged.
                    if (jobs is not { Count: 0 } && Outcomes.Read(jobs) is { } outcome)
                        return outcome.Kind switch
                        {
                            OutcomeKind.Passed => (true, $"Run {runId}: the tests passed."),
                            // The setup is sound, which is what a dry run judges; the repository is not ready, which is a
                            // different thing, and says so here rather than in the verdict.
                            OutcomeKind.Failed => (true, $"Run {runId}: the tests **failed**. The contract holds, so a real "
                                + "cycle would open the `main-broken` lock for this commit: make `main` green before setting "
                                + "`enabled: true`."),
                            _ => (false, $"Run {runId}: {outcome.Description}")
                        };
                    if (now() >= deadline)
                        return (false, $"The `main-watcher` job of run {runId} had not completed {allowed.TotalMinutes:0} "
                            + $"minutes after the dispatch. Raise `timeout` if the suite needs longer than {target.Timeout} "
                            + "minutes, in the entry and in the caller together.");
                    await wait(Poll, token);
                }
            }),

            // ADR-007: the reports are what a cycle reads a failure from, so they are validated here against the same schema,
            // through the same reader, that every cycle will use.
            ("CTRF reports", async token =>
            {
                var result = await github.Reports(repo, runId, token);
                if (!result.Known)
                    return (false, $"The `main-watcher-ctrf` artifact of run {runId} is missing, holds no report, or holds one "
                        + $"that is not valid CTRF. Check that `results_glob` (`{target.ResultsGlob}`) matches what the suite "
                        + "writes, and that the reports are CTRF (ADR-007).");
                var failures = result.Failures.Count == 0 ? "no failing tests"
                    : $"{result.Failures.Count} failing test(s): "
                        + string.Join("; ", result.Failures.Take(5).Select(f => $"`{f.Name}`"));
                var timing = result.Timing is { } t ? $"; suite time {t.WallClockMs} ms" : "";
                return (true, $"The `main-watcher-ctrf` artifact validates against the CTRF schema: {failures}{timing}.");
            })
        ];

        var steps = new List<DryRunStep>();
        foreach (var (name, check) in checks)
        {
            (bool Passed, string Detail) result;
            // Every failure is a finding about the target's setup, an API error included: the report says which check it
            // stopped at, which is what the person onboarding needs, and a stack trace is not.
            try { result = await check(ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { result = (false, e.Message); }
            var step = new DryRunStep(name, result.Passed, result.Detail);
            steps.Add(step);
            log?.Invoke($"{(step.Passed ? "ok    " : "FAILED")} {step.Name}: {step.Detail}");
            if (!step.Passed) break;
        }
        return new(steps);
    }
}
