using System.Net.Http.Headers;
using MainWatcher.Core;

// The App installation's rate-limit budget is shared by every target, so each cycle says what it left and alerts when it
// runs low or refuses the cycle (R-13, #60), whichever step the cycle failed at.
GitHubGateway? budget = null;
Alerts? alerts = null;
// Outlives the cycle, since the budget alert is raised after it.
HttpClient? alertHttp = null;
var exit = await Cycle();
if (budget?.Budget is { } left) Console.WriteLine($"API budget of this installation: {left}.");
if (budget?.Requests is { Count: > 0 } sent)
    Console.WriteLine($"API requests this cycle: {sent.Sum(r => r.Count)}; most: "
        + string.Join(", ", sent.Take(6).Select(r => $"{r.Count} {r.Endpoint}")) + ".");
if (budget is not null && alerts is not null)
{
    try
    {
        using var alertTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        if (await InstallationBudget.Judge(Environment.GetEnvironmentVariable("MW_TARGET") ?? "", budget.Budget, budget.RateLimited,
            alerts, DateTimeOffset.UtcNow, alertTimeout.Token) is { } raised)
            Console.WriteLine($"Alert raised: {raised}.");
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Alert not raised: the API budget alert: {e.Message}");
        exit = 1;
    }
}
alertHttp?.Dispose();
return exit;

async Task<int> Cycle()
{
    try
    {
        var config = TargetConfiguration.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("MW_TARGETS_FILE") ?? "targets.yml"));
        if (args is ["--list-targets"])
        {
            // The sweep's matrix: every enabled target, from the same parser the cycles use. Each leg validates its own target again.
            File.AppendAllLines(Required("GITHUB_OUTPUT"),
                [$"targets={System.Text.Json.JsonSerializer.Serialize(config.Targets.Where(t => t.Enabled).Select(t => t.Repo))}"]);
            return 0;
        }
        if (args is ["--check-targets"])
        {
            // Watcher repo CI (docs/onboarding.md): the committed list, read by the parser the cycles use, so an entry that
            // would break a sweep fails the pull request that adds it rather than the next hour's sweep.
            foreach (var entry in config.Targets)
                Console.WriteLine($"{entry.Repo}: {(entry.Enabled ? "enabled" : "disabled")}, test_command \"{entry.TestCommand}\", "
                    + $"results_glob \"{entry.ResultsGlob}\", timeout {entry.Timeout} min, poll_interval {entry.PollInterval} min, "
                    + $"notify [{string.Join(", ", entry.Notify)}].");
            Console.WriteLine($"{config.Targets.Count} target(s) parsed; lock_lease {config.LockLease} min.");
            return 0;
        }
        var repo = Environment.GetEnvironmentVariable("MW_TARGET") ?? "";
        var target = config.Targets.SingleOrDefault(t => t.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("MW_TARGET must identify a configured target.");
        // A dry run is what an entry gets before it is enabled (docs/onboarding.md), so it is the one mode that accepts a
        // disabled target. It creates no check run, so nothing it does can lock the target.
        var dryRun = args is ["--dry-run"] or ["--validate-target", "--dry-run"];
        if (!target.Enabled && !dryRun)
        {
            if (args is ["--validate-target"]) throw new InvalidOperationException("Target disabled; no App token requested.");
            Console.WriteLine("Target disabled.");
            return 0;
        }
        if (args is ["--validate-target"] or ["--validate-target", "--dry-run"])
        {
            var parts = target.Repo.Split('/');
            File.AppendAllLines(Required("GITHUB_OUTPUT"), [$"owner={parts[0]}", $"repo={parts[1]}"]);
            return 0;
        }
        if (dryRun)
        {
            using var dryHttp = Client(Required("GH_TOKEN"));
            var dryGithub = new GitHubGateway(dryHttp, long.Parse(Required("MW_APP_ID")), log: Console.WriteLine);
            budget = dryGithub;
            // The dry run stops itself within the hour its App token lasts, so this only catches a wait that is not waiting.
            using var dryTimeout = new CancellationTokenSource(DryRun.TokenWindow + TimeSpan.FromMinutes(5));
            var report = await new DryRun(dryGithub, log: Console.WriteLine).Run(target, dryTimeout.Token);
            Summarise(target.Repo, report);
            Console.WriteLine(report.Passed
                ? $"Dry run of {target.Repo} passed. Next: make the gate a required merge-queue check, run TS-S5 once, then set enabled: true."
                : $"Dry run of {target.Repo} failed at \"{report.Steps[^1].Name}\".");
            return report.Passed ? 0 : 1;
        }
        if (args.Length != 0)
            throw new ArgumentException("Usage: MainWatcher.Watcher [--list-targets|--check-targets|--dry-run|--validate-target [--dry-run]]");
        using var http = Client(Required("GH_TOKEN"));
        // Alerts go to the watcher repo with its own workflow token; the App token is scoped to the target.
        alertHttp = Client(Required("MW_ALERT_TOKEN"));
        var appId = long.Parse(Required("MW_APP_ID"));
        var github = new GitHubGateway(http, appId, log: Console.WriteLine);
        budget = github;
        var alertRepo = Required("MW_ALERT_REPO");
        var watcherGithub = new GitHubGateway(alertHttp, appId);
        alerts = new Alerts(watcherGithub, alertRepo);
        var botLogin = Environment.GetEnvironmentVariable("MW_BOT_LOGIN") is { Length: > 0 } app ? app : Reporter.DefaultBotLogin;
        // Sandbox fault injection (TS-S16 (g) and (h)): a shorter queue deadline, so a job that will never get a runner is
        // cancelled in minutes, and a switch that makes every cancel fail. The worker must be given the same deadline. Each
        // switch applies to every target or, written owner/repo=value, to one (SandboxSwitch).
        var queueDeadline = StaleRun.ConfiguredQueueDeadline(Environment.GetEnvironmentVariable("MW_QUEUE_DEADLINE_MINUTES"), repo);
        // Sandbox fault injection (TS-S14, TS-S17 (b)): exit right after the named issue write, so the next cycle replays it.
        var exitAfter = SandboxSwitch.For(Environment.GetEnvironmentVariable("MW_SANDBOX_EXIT_AFTER"), repo);
        void ExitAfter(string write)
        {
            if (!exitAfter.Contains(write)) return;
            Console.WriteLine($"MW_SANDBOX_EXIT_AFTER: exiting after the {write} write.");
            Environment.Exit(3);
        }
        var planner = new Planner(github, alerts: alerts, botLogin: botLogin, queueDeadline: queueDeadline,
            cancelsRuns: !SandboxSwitch.For(Environment.GetEnvironmentVariable("MW_SANDBOX_REFUSE_CANCEL"), repo).Contains("true"),
            afterWrite: ExitAfter);
        var reporter = new Reporter(github, alerts, botLogin, afterWrite: ExitAfter);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        // The hourly backup sweep (C-7, ADR-010): every enabled target gets a cycle, and this one also reports what only the
        // sweep looks for. A dispatch with no target sweeps too, which is how the scenario is run by hand.
        var sweep = Environment.GetEnvironmentVariable("MW_SWEEP") == "true"
            ? new Sweep(github, watcherGithub, alertRepo, alerts, new WorkFinder(queueDeadline: _ => queueDeadline, botLogin: botLogin)) : null;
        var sweepFailed = false;
        if (sweep is not null)
        {
            // Before the cycle, which is what does the work the trigger worker left waiting.
            try
            {
                if (await sweep.WorkerDown(target, timeout.Token) is { } waiting)
                    Console.WriteLine($"Sweep: the trigger worker appears down; {waiting.Reason}.");
            }
            catch (Exception e) when (!timeout.IsCancellationRequested)
            {
                sweepFailed = true;
                Console.Error.WriteLine($"Sweep: the waiting-work check failed: {e.Message}");
            }
        }
        var recoveryFailed = false;
        foreach (var pending in (await github.Checks(repo, timeout.Token)).Where(c => c.Status != "completed"))
        {
            try
            {
                var check = await planner.Recover(repo, pending, timeout.Token);
                if (check.Status == "completed" || await reporter.Report(target, check, timeout.Token))
                {
                    Console.WriteLine($"Reported check {check.Id}.");
                    continue;
                }
                // Nothing to report yet: the target run may instead have passed a deadline and need stopping (ADR-013 point 5).
                Console.WriteLine(await planner.Stop(target, check, timeout.Token) ?? $"Check {check.Id} remains pending.");
            }
            catch (Exception e) when (!timeout.IsCancellationRequested)
            {
                recoveryFailed = true;
                Console.Error.WriteLine($"Check {pending.Id}: {e.Message}");
            }
        }
        // ADR-014: renewal is what tells the gate this lock is still maintained, so it happens on every cycle that processes the
        // target, before planning and whether or not anything else in the cycle worked.
        var renewFailed = false;
        try { foreach (var line in await planner.Renew(target, timeout.Token)) Console.WriteLine(line); }
        catch (Exception e) when (!timeout.IsCancellationRequested)
        {
            renewFailed = true;
            Console.Error.WriteLine($"Lease renewal failed: {e.Message}");
        }
        // ADR-016: the queue sweep discharges what this cycle's report or renewal recorded, and what an earlier one left owed. It
        // comes before planning, because a group queued before the lock merges onto a red `main` while it waits, and a test that
        // starts a minute later costs nothing. A failure leaves the obligation in the issue, so the next cycle sweeps again.
        var queueSweepFailed = false;
        try { foreach (var line in await planner.SweepQueue(target, reporter.Opened, timeout.Token)) Console.WriteLine(line); }
        catch (Exception e) when (!timeout.IsCancellationRequested)
        {
            queueSweepFailed = true;
            Console.Error.WriteLine($"Queue sweep failed: {e.Message}");
        }
        foreach (var walk in reporter.WalkBacks)
        {
            // ADR-003: the walk-back length is logged in the job summary; revisit it if it regularly nears 50.
            var line = $"Check {walk.CheckId}: walked back {walk.CommitsChecked} commits for the last green run (push list: {walk.Source}).";
            Console.WriteLine(line);
            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary) File.AppendAllLines(summary, [line]);
        }
        foreach (var failure in reporter.AlertFailures) Console.Error.WriteLine($"Alert not raised: {failure}");
        // A sweep alert is never a required write (ADR-010): a failed one fails the run at the end, but the backup testing this
        // run exists to do still happens, since it is exactly when the worker is down that nothing else will.
        if (recoveryFailed || renewFailed || reporter.AlertFailures.Count > 0) return 1;
        var planned = await planner.Plan(target, Environment.GetEnvironmentVariable("MW_FORCE") == "true", timeout.Token);
        Console.WriteLine(planned is null ? "No eligible head." : string.IsNullOrEmpty(planned.ExternalId)
            ? $"Check {planned.Id} awaits dispatch recovery." : $"Started check {planned.Id}, target run {planned.ExternalId}.");
        // The "head untestable" alert (ADR-017) is the Planner's only write when it starts nothing; a failure to raise it is
        // reported at the end, so it never stops the rest of the cycle.
        foreach (var failure in planner.AlertFailures) Console.Error.WriteLine($"Alert not raised: {failure}");
        // After planning, so a lock whose comments cannot be written (for example, a locked conversation) never stops testing.
        var overrides = await reporter.NoteOverrides(target, timeout.Token);
        if (overrides > 0) Console.WriteLine($"Posted {overrides} override comment(s) on locks closed by hand.");
        // Reconciliation (ADR-008, ADR-015) runs after planning for the same reason: it is a reporting obligation, not a testing
        // one, and it makes the most API calls of anything in a cycle. A failure fails the run, so the worker asks again.
        var reconcileFailed = false;
        try { foreach (var line in await planner.Reconcile(target, timeout.Token, reporter.ClosedSeen)) Console.WriteLine(line); }
        catch (Exception e) when (!timeout.IsCancellationRequested)
        {
            reconcileFailed = true;
            Console.Error.WriteLine($"Reconciliation failed: {e.Message}");
        }
        if (sweep is not null)
        {
            // Last: a gate that failed open is a secondary signal (ADR-008), and never delays testing or reporting.
            try
            {
                var open = await sweep.GateFailedOpen(target, timeout.Token);
                Console.WriteLine(open > 0 ? $"Sweep: reported {open} merge group(s) whose gate failed open." : "Sweep: no gate failed open.");
            }
            catch (Exception e) when (!timeout.IsCancellationRequested)
            {
                sweepFailed = true;
                Console.Error.WriteLine($"Sweep: the gate fail-open check failed: {e.Message}");
            }
        }
        // A queue sweep that failed fails the run, but never held the test back: the obligation stays in the lock issue, so the
        // worker asks for another cycle whether or not this one is reported as having failed (ADR-016).
        return sweepFailed || queueSweepFailed || reconcileFailed || planner.AlertFailures.Count > 0 ? 1 : 0;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Watcher failed: {e.Message}");
        return 1;
    }
}

/// <summary>The dry run's checks in the job summary, so the report outlives the log (docs/onboarding.md).</summary>
static void Summarise(string repo, DryRunReport report)
{
    if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is not { Length: > 0 } path) return;
    File.AppendAllLines(path, [$"## Dry run of `{repo}`: {(report.Passed ? "passed" : "failed")}", ""]);
    File.AppendAllLines(path, report.Steps.Select(s => $"- **{s.Name}** — {(s.Passed ? "ok" : "**failed**")}: {s.Detail}"));
}

static HttpClient Client(string token)
{
    var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("MainWatcher/1.0");
    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    return http;
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"{name} is required.");
