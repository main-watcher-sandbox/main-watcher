namespace MainWatcher.Core;

public sealed class Planner(IGitHubGateway github, Func<DateTimeOffset>? clock = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public async Task<CheckRun?> Plan(Target target, bool force, CancellationToken ct)
    {
        if (!target.Enabled) return null;
        var sha = await github.MainHead(target.Repo, ct);
        var checks = await github.Checks(target.Repo, ct);
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        if (!Eligibility.CanStart(sha, checks, TimeSpan.FromMinutes(target.PollInterval), now, force)) return null;
        var check = await github.CreateCheck(target.Repo, sha, now, ct);
        // Never retry a dispatch POST: a lost response may still have started the workflow.
        var runId = await github.Dispatch(target.Repo, sha, check.Id, ct);
        for (var attempt = 0; runId is null && attempt < 6; attempt++)
        {
            runId = await FindRun(target.Repo, check, ct);
            if (runId is null) await (delay ?? Task.Delay)(TimeSpan.FromSeconds(5), ct);
        }
        if (runId is null) throw new InvalidOperationException($"Dispatch for check {check.Id} is not visible; left pending for recovery.");
        await github.Link(target.Repo, check.Id, runId.Value, ct);
        return check with { ExternalId = runId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }

    public async Task<long?> FindRun(string repo, CheckRun check, CancellationToken ct)
    {
        // The caller exposes its sha input in run-name. head_sha describes the dispatch
        // ref, which can move independently of the commit under test (R-14).
        var matches = (await github.Runs(repo, check.StartedAt.AddSeconds(-2), ct))
            .Where(r => r.Title == $"main-watcher-tests {check.Sha}"
                && r.CreatedAt >= check.StartedAt.AddSeconds(-2)).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }
}

public sealed record TestOutcome(string Conclusion, string Description);

public static class Outcomes
{
    public static TestOutcome? Read(IReadOnlyList<WorkflowJob>? jobs)
    {
        if (jobs is null) return new("neutral", "outcome unknown: target run was deleted");
        var matches = jobs.Where(j => j.Name == "main-watcher" || j.Name.EndsWith(" / main-watcher", StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1) return new("neutral", "outcome contract broken: multiple main-watcher jobs");
        if (matches.Length == 0) return new("neutral", "outcome contract broken: main-watcher job missing from completed run");
        var job = matches[0];
        if (job.Status != "completed") return null;
        var tests = job.Steps.Where(s => s.Name == "main-watcher-test").ToArray();
        var markers = job.Steps.Where(s => s.Name == "main-watcher-tests-finished").ToArray();
        if (tests.Length > 1 || markers.Length > 1) return new("neutral", "outcome contract broken: duplicate step names");
        if (markers.Length == 0 || markers[0].Conclusion != "success") return new("neutral", "Infrastructure error: tests did not finish");
        return tests.SingleOrDefault()?.Conclusion switch
        {
            "success" => new("success", "Tests passed"),
            "failure" => new("failure", "Tests failed"),
            _ => new("neutral", "outcome contract broken: finished marker without a test result")
        };
    }
}

public sealed class Reporter(IGitHubGateway github)
{
    public async Task<bool> Report(string repo, CheckRun check, CancellationToken ct)
    {
        if (check.Status == "completed" || !long.TryParse(check.ExternalId, out var runId)) return false;
        var outcome = Outcomes.Read(await github.Jobs(repo, runId, ct));
        if (outcome is null) return false;
        var summary = outcome.Description + $"\n\n[Target run](https://github.com/{repo}/actions/runs/{runId})";
        if (outcome.Conclusion is "success" or "failure")
        {
            var reports = await github.Reports(repo, runId, ct);
            if (outcome.Conclusion == "failure")
                summary += reports.Known && reports.Failures.Count > 0
                    ? "\n\n" + string.Join("\n", reports.Failures.Select(f => $"- {Escape(f.Name)} ({Escape(f.Suite)}): {Escape(f.Message)}"))
                    : "\n\nfailing tests unknown";
            else if (reports.Failures.Count > 0) summary += "\n\nWarning: CTRF reports failures despite a successful test step.";
        }
        // GitHub caps check output at 65535 bytes. Conservatively bound UTF-16 length.
        if (summary.Length > 15000) summary = summary[..15000] + "\n\nOutput truncated; see target run.";
        await github.Complete(repo, check.Id, outcome.Conclusion, summary, ct);
        return true;
    }

    static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text)
        .Replace("\r", " ").Replace("\n", " ").Replace("`", "\\`").Replace("*", "\\*").Replace("[", "\\[");
}
