using System.Net.Http.Headers;
using MainWatcher.Core;

try
{
    var config = TargetConfiguration.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("MW_TARGETS_FILE") ?? "targets.yml"));
    var repo = Environment.GetEnvironmentVariable("MW_TARGET") ?? "";
    var target = config.Targets.SingleOrDefault(t => t.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("MW_TARGET must identify a configured target.");
    if (!target.Enabled) { Console.WriteLine("Target disabled."); return 0; }
    using var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Required("GH_TOKEN"));
    http.DefaultRequestHeaders.UserAgent.ParseAdd("MainWatcher/1.0");
    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    var github = new GitHubGateway(http, long.Parse(Required("MW_APP_ID")));
    var planner = new Planner(github);
    var reporter = new Reporter(github);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    var recoveryFailed = false;
    foreach (var pending in (await github.Checks(repo, timeout.Token)).Where(c => c.Status != "completed"))
    {
        try
        {
            var check = await planner.Recover(repo, pending, timeout.Token);
            Console.WriteLine(check.Status == "completed" || await reporter.Report(repo, check, timeout.Token)
                ? $"Reported check {check.Id}." : $"Check {check.Id} remains pending.");
        }
        catch (Exception e) when (!timeout.IsCancellationRequested)
        {
            recoveryFailed = true;
            Console.Error.WriteLine($"Check {pending.Id}: {e.Message}");
        }
    }
    if (recoveryFailed) return 1;
    var planned = await planner.Plan(target, Environment.GetEnvironmentVariable("MW_FORCE") == "true", timeout.Token);
    Console.WriteLine(planned is null ? "No eligible head." : string.IsNullOrEmpty(planned.ExternalId)
        ? $"Check {planned.Id} awaits dispatch recovery." : $"Started check {planned.Id}, target run {planned.ExternalId}.");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Watcher failed: {e.Message}");
    return 1;
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"{name} is required.");
