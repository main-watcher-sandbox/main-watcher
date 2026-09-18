using System.Net.Http.Headers;
using MainWatcher.Core;

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
    var repo = Environment.GetEnvironmentVariable("MW_TARGET") ?? "";
    var target = config.Targets.SingleOrDefault(t => t.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("MW_TARGET must identify a configured target.");
    if (!target.Enabled)
    {
        if (args is ["--validate-target"]) throw new InvalidOperationException("Target disabled; no App token requested.");
        Console.WriteLine("Target disabled.");
        return 0;
    }
    if (args is ["--validate-target"])
    {
        var parts = target.Repo.Split('/');
        File.AppendAllLines(Required("GITHUB_OUTPUT"), [$"owner={parts[0]}", $"repo={parts[1]}"]);
        return 0;
    }
    if (args.Length != 0) throw new ArgumentException("Usage: MainWatcher.Watcher [--validate-target|--list-targets]");
    using var http = Client(Required("GH_TOKEN"));
    // Alerts go to the watcher repo with its own workflow token; the App token is scoped to the target.
    using var alertHttp = Client(Required("MW_ALERT_TOKEN"));
    var appId = long.Parse(Required("MW_APP_ID"));
    var github = new GitHubGateway(http, appId, log: Console.WriteLine);
    var alertRepo = Required("MW_ALERT_REPO");
    var watcherGithub = new GitHubGateway(alertHttp, appId);
    var alerts = new Alerts(watcherGithub, alertRepo);
    var botLogin = Environment.GetEnvironmentVariable("MW_BOT_LOGIN") is { Length: > 0 } app ? app : Reporter.DefaultBotLogin;
    // Sandbox fault injection (TS-S16 (g) and (h)): a shorter queue deadline, so a job that will never get a runner is
    // cancelled in minutes, and a switch that makes every cancel fail. The worker must be given the same deadline.
    var queueDeadline = StaleRun.ConfiguredQueueDeadline(Environment.GetEnvironmentVariable("MW_QUEUE_DEADLINE_MINUTES"));
    var planner = new Planner(github, alerts: alerts, botLogin: botLogin, queueDeadline: queueDeadline,
        cancelsRuns: Environment.GetEnvironmentVariable("MW_SANDBOX_REFUSE_CANCEL") != "true");
    // Sandbox fault injection (TS-S14): exit right after the named Reporter write, so the next cycle replays the report.
    var exitAfter = Environment.GetEnvironmentVariable("MW_SANDBOX_EXIT_AFTER") ?? "";
    var reporter = new Reporter(github, alerts, botLogin,
        afterWrite: write =>
        {
            if (!exitAfter.Split(',', StringSplitOptions.TrimEntries).Contains(write)) return;
            Console.WriteLine($"MW_SANDBOX_EXIT_AFTER: exiting after the Reporter's {write} write.");
            Environment.Exit(3);
        });
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    // The hourly backup sweep (C-7, ADR-010): every enabled target gets a cycle, and this one also reports what only the
    // sweep looks for. A dispatch with no target sweeps too, which is how the scenario is run by hand.
    var sweep = Environment.GetEnvironmentVariable("MW_SWEEP") == "true"
        ? new Sweep(github, watcherGithub, alertRepo, alerts, new WorkFinder()) : null;
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
    if (recoveryFailed || reporter.AlertFailures.Count > 0) return 1;
    var planned = await planner.Plan(target, Environment.GetEnvironmentVariable("MW_FORCE") == "true", timeout.Token);
    Console.WriteLine(planned is null ? "No eligible head." : string.IsNullOrEmpty(planned.ExternalId)
        ? $"Check {planned.Id} awaits dispatch recovery." : $"Started check {planned.Id}, target run {planned.ExternalId}.");
    // The "head untestable" alert (ADR-017) is the Planner's only write when it starts nothing; a failure to raise it is
    // reported at the end, so it never stops the rest of the cycle.
    foreach (var failure in planner.AlertFailures) Console.Error.WriteLine($"Alert not raised: {failure}");
    // After planning, so a lock whose comments cannot be written (for example, a locked conversation) never stops testing.
    var overrides = await reporter.NoteOverrides(target, timeout.Token);
    if (overrides > 0) Console.WriteLine($"Posted {overrides} override comment(s) on locks closed by hand.");
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
    return sweepFailed || planner.AlertFailures.Count > 0 ? 1 : 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Watcher failed: {e.Message}");
    return 1;
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
