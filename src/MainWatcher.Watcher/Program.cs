using System.Net.Http.Headers;
using MainWatcher.Core;

try
{
    var config = TargetConfiguration.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("MW_TARGETS_FILE") ?? "targets.yml"));
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
    if (args.Length != 0) throw new ArgumentException("Usage: MainWatcher.Watcher [--validate-target]");
    using var http = Client(Required("GH_TOKEN"));
    // Alerts go to the watcher repo with its own workflow token; the App token is scoped to the target.
    using var alertHttp = Client(Required("MW_ALERT_TOKEN"));
    var appId = long.Parse(Required("MW_APP_ID"));
    var github = new GitHubGateway(http, appId, log: Console.WriteLine);
    var planner = new Planner(github);
    // Sandbox fault injection (TS-S14): exit right after the named Reporter write, so the next cycle replays the report.
    var exitAfter = Environment.GetEnvironmentVariable("MW_SANDBOX_EXIT_AFTER") ?? "";
    var reporter = new Reporter(github, new Alerts(new GitHubGateway(alertHttp, appId), Required("MW_ALERT_REPO")),
        Environment.GetEnvironmentVariable("MW_BOT_LOGIN") is { Length: > 0 } bot ? bot : Reporter.DefaultBotLogin,
        afterWrite: write =>
        {
            if (!exitAfter.Split(',', StringSplitOptions.TrimEntries).Contains(write)) return;
            Console.WriteLine($"MW_SANDBOX_EXIT_AFTER: exiting after the Reporter's {write} write.");
            Environment.Exit(3);
        });
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    var recoveryFailed = false;
    foreach (var pending in (await github.Checks(repo, timeout.Token)).Where(c => c.Status != "completed"))
    {
        try
        {
            var check = await planner.Recover(repo, pending, timeout.Token);
            Console.WriteLine(check.Status == "completed" || await reporter.Report(target, check, timeout.Token)
                ? $"Reported check {check.Id}." : $"Check {check.Id} remains pending.");
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
    if (recoveryFailed || reporter.AlertFailures.Count > 0) return 1;
    var planned = await planner.Plan(target, Environment.GetEnvironmentVariable("MW_FORCE") == "true", timeout.Token);
    Console.WriteLine(planned is null ? "No eligible head." : string.IsNullOrEmpty(planned.ExternalId)
        ? $"Check {planned.Id} awaits dispatch recovery." : $"Started check {planned.Id}, target run {planned.ExternalId}.");
    // After planning, so a lock whose comments cannot be written (for example, a locked conversation) never stops testing.
    var overrides = await reporter.NoteOverrides(target, timeout.Token);
    if (overrides > 0) Console.WriteLine($"Posted {overrides} override comment(s) on locks closed by hand.");
    return 0;
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
