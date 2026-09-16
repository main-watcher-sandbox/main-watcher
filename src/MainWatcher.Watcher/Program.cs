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
    foreach (var pending in (await github.Checks(repo, timeout.Token)).Where(c => c.Status != "completed"))
    {
        var check = pending;
        if (string.IsNullOrEmpty(check.ExternalId))
        {
            var id = await planner.FindRun(repo, check, timeout.Token);
            if (id is null) throw new InvalidOperationException($"Cannot uniquely recover run for check {check.Id}; dispatch remains blocked.");
            await github.Link(repo, check.Id, id.Value, timeout.Token);
            check = check with { ExternalId = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }
        Console.WriteLine(await reporter.Report(repo, check, timeout.Token) ? $"Reported check {check.Id}." : $"Check {check.Id} remains pending.");
    }
    var planned = await planner.Plan(target, Environment.GetEnvironmentVariable("MW_FORCE") == "true", timeout.Token);
    Console.WriteLine(planned is null ? "No eligible head." : $"Started check {planned.Id}, target run {planned.ExternalId}.");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Watcher failed: {e.Message}");
    return 1;
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"{name} is required.");
