using System.Text.RegularExpressions;
using MainWatcher.Scenarios.Infra;

namespace MainWatcher.Scenarios;

/// <summary>
/// The commit statuses a run leaves on the MainWatcher commit it tested, which is how a release knows the suite passed
/// (TS-001 §5): <c>scenario-suite</c> for a full run, and <c>scenario-suite/ts-s13</c> whenever TS-S13 ran, which a change to
/// the reporter pin needs (ADR-018). Only a run that gave the sandbox exactly that commit may post them.
/// </summary>
public sealed partial class Statuses(GitHub github, string root, Log log)
{
    public const string SuiteContext = "scenario-suite";
    public const string ReporterContext = "scenario-suite/ts-s13";

    public async Task Post(string commit, bool unqualified, bool fullSuite, bool passed, IReadOnlyCollection<UnitResult> results,
        TimeSpan duration, CancellationToken ct)
    {
        if (unqualified)
        {
            log.Warn("No status posted: the sandbox was not given this exact commit (uncommitted changes, or --no-deploy).");
            return;
        }
        var remote = (await Shell.Run("git", ["remote", "get-url", "origin"], ct, root)).Trim();
        if (RepoPattern().Match(remote) is not { Success: true } match)
        {
            log.Warn($"No status posted: origin ({remote}) is not a GitHub repo.");
            return;
        }
        var repo = match.Groups["repo"].Value;
        if (await github.Find($"repos/{repo}/commits/{commit}", ct) is null)
        {
            log.Warn($"No status posted: {commit[..7]} is not on {repo}; push it and run the suite again.");
            return;
        }
        if (fullSuite)
        {
            var failed = results.Where(r => !r.Passed).SelectMany(r => r.Scenario.Covers).ToList();
            await Send(repo, commit, SuiteContext, passed, passed
                ? $"{results.Count} units passed in {duration.TotalMinutes:0} min"
                : $"Failed: {string.Join(", ", failed)}", ct);
        }
        if (results.FirstOrDefault(r => r.Scenario.Covers.Any(c => c.StartsWith("TS-S13", StringComparison.Ordinal))) is { } ts13)
            await Send(repo, commit, ReporterContext, ts13.Passed, ts13.Passed ? "TS-S13 passed" : "TS-S13 failed", ct);
    }

    async Task Send(string repo, string commit, string context, bool success, string description, CancellationToken ct)
    {
        await github.Post($"repos/{repo}/statuses/{commit}", new
        {
            state = success ? "success" : "failure",
            context,
            description = description.Length > 140 ? description[..139] + "…" : description
        }, ct);
        log.Info($"Posted {context} = {(success ? "success" : "failure")} on {repo}@{commit[..7]}.");
    }

    [GeneratedRegex(@"github\.com[:/](?<repo>[^/]+/[^/]+?)(\.git)?$")]
    private static partial Regex RepoPattern();
}
