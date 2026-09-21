using MainWatcher.Scenarios;
using MainWatcher.Scenarios.Infra;
using MainWatcher.Scenarios.Sandbox;

// The scenario suite (TS-001 §5): TS-S1–S18 against the sandbox, from one entry point, sandbox/run-scenarios.sh.
// A full run that passes marks the commit under test with the scenario-suite status, which release.yml requires.

var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("""
        Usage: sandbox/run-scenarios.sh [--only TS-S13,TS-S4] [--no-deploy] [--targets N] [--no-status] [--list]

          --only       run only the units covering these scenarios (a prefix: TS-S16 is every TS-S16 unit)
          --no-deploy  skip publishing, the replica push, the worker image and seeding: test what the sandbox already runs
          --targets    how many pool targets to use (2 to 10, default 6): sample-target, sample-target-2 onwards, and
                       always sample-target-10, which the worker gives a short queue deadline
          --no-status  post no commit status, even when the run qualifies
          --list       list the units and exit
        """);
    return 2;
}

var catalogue = Catalogue.All();
if (options.List)
{
    foreach (var unit in catalogue) Console.WriteLine($"{unit.Name,-36} {unit.Phase,-7} ~{unit.Estimate.TotalMinutes,3:0} min  {unit.Title}");
    return 0;
}
var selected = options.Only is null ? catalogue
    : catalogue.Where(u => u.Covers.Any(c => options.Only.Any(o => c.StartsWith(o, StringComparison.OrdinalIgnoreCase)))).ToList();
if (selected.Count == 0) { Console.Error.WriteLine("No unit covers those scenarios."); return 2; }

var root = Repository.Root();
var commit = (await Shell.Run("git", ["rev-parse", "HEAD"], CancellationToken.None, root)).Trim();
var dirty = (await Shell.Run("git", ["status", "--porcelain"], CancellationToken.None, root)).Trim().Length > 0;
Directory.CreateDirectory(RunInfo.OutDir = Path.Combine(root, "sandbox", "scenarios", "out", $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{commit[..7]}"));
var log = new Log("suite", Path.Combine(RunInfo.OutDir, "suite.log"));
if (dirty && !options.NoDeploy)
{
    log.Warn("The working tree has uncommitted changes. The sandbox is given HEAD's tree, and no status will be posted.");
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var token = Environment.GetEnvironmentVariable("GH_TOKEN") is { Length: > 0 } fromEnv ? fromEnv
    : (await Shell.Run("gh", ["auth", "token"], stop.Token)).Trim();
using var github = new GitHub(token);
var me = (await github.Get("user", stop.Token))!["login"]!.GetValue<string>();
var templates = new Templates(root);
// Every target's cycles share the main-watcher App installation's 5000 requests an hour. Ten targets at once spent it all
// in 39 minutes in the fourth run (#25), so six is the default. The last is always sample-target-10, for TS-S16 (g).
var pool = Enumerable.Range(1, options.Targets - 1).Append(10)
    .Select(n => new Target(github, $"{SandboxOrg.Org}/sample-target{(n == 1 ? "" : $"-{n}")}", templates)).ToList();
var sandbox = new SandboxOrg(github, templates, new Replica(github, $"{SandboxOrg.Org}/main-watcher"), new Worker(SandboxOrg.Org), pool, me);
log.Info($"MainWatcher@{commit[..7]}: {selected.Count} of {catalogue.Count} units on {pool.Count} targets, as {me}. Output: {RunInfo.OutDir}");

var clock = System.Diagnostics.Stopwatch.StartNew();
var preparation = new Preparation(sandbox, root, log);
try
{
    if (!options.NoDeploy) await preparation.Deploy(commit, stop.Token);
    await preparation.Configure(stop.Token);
}
catch (Exception e) when (!stop.IsCancellationRequested)
{
    log.Warn($"Preparation failed, so no scenario ran: {e.Message}");
    return 1;
}
log.Info($"Prepared in {clock.Elapsed.TotalMinutes:0} min.");

var suite = new Suite(sandbox, selected, RunInfo.OutDir, log);
var runClock = System.Diagnostics.Stopwatch.StartNew();
await suite.Run(stop.Token);
var notRun = selected.Where(u => suite.Results.All(r => r.Scenario != u)).ToList();
var report = suite.Report(commit, runClock.Elapsed, notRun);
await File.WriteAllTextAsync(Path.Combine(RunInfo.OutDir, "report.md"), report);
await File.WriteAllTextAsync(Path.Combine(RunInfo.OutDir, "results.json"), suite.Json(commit, runClock.Elapsed));
var passed = notRun.Count == 0 && suite.Results.All(r => r.Passed);
log.Info($"{(passed ? "PASSED" : "FAILED")}: {suite.Results.Count(r => r.Passed)} of {selected.Count} units in {runClock.Elapsed.TotalMinutes:0} min " +
    $"(plus {(clock.Elapsed - runClock.Elapsed).TotalMinutes:0} min preparing). Report: {Path.Combine(RunInfo.OutDir, "report.md")}");

if (!options.NoStatus)
{
    try
    {
        var statuses = new Statuses(github, root, log);
        await statuses.Post(commit, dirty || options.NoDeploy, selected.Count == catalogue.Count, passed, suite.Results, runClock.Elapsed, stop.Token);
    }
    catch (Exception e) when (!stop.IsCancellationRequested) { log.Warn($"No status posted: {e.Message}"); }
}
return passed ? 0 : 1;

/// <summary>This run's output folder.</summary>
static class RunInfo
{
    public static string OutDir { get; set; } = "";
}

/// <summary>The command line.</summary>
sealed record Options(string[]? Only, bool NoDeploy, int Targets, bool NoStatus, bool List)
{
    public static Options? Parse(string[] args)
    {
        string[]? only = null;
        bool noDeploy = false, noStatus = false, list = false;
        var targets = 6;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--only" when i + 1 < args.Length:
                    only = args[++i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "--no-deploy": noDeploy = true; break;
                case "--no-status": noStatus = true; break;
                case "--list": list = true; break;
                case "--targets" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n is >= 2 and <= 10:
                    targets = n;
                    i++;
                    break;
                default: return null;
            }
        }
        return new(only, noDeploy, targets, noStatus, list);
    }
}

/// <summary>Where the MainWatcher checkout is: the suite reads its templates and runs its scripts from there.</summary>
static class Repository
{
    public static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MainWatcher.slnx"))) return dir.FullName;
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MainWatcher.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Run the suite from a MainWatcher checkout.");
    }
}
