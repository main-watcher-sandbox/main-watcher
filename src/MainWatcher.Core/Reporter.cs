using System.Globalization;

namespace MainWatcher.Core;

/// <summary>
/// Reports the ADR-013 outcome with ADR-007 CTRF failure details. The issue side is written first
/// and the check run is completed last: red opens the lock issue, green closes it (ADR-004).
/// </summary>
public sealed class Reporter(IGitHubGateway github, Alerts? alerts = null, string botLogin = Reporter.DefaultBotLogin,
    Func<DateTimeOffset>? clock = null)
{
    public const string DefaultBotLogin = "main-watcher[bot]";
    public const string LockLabel = "main-broken";
    /// <summary><c>lock_lease</c> (ADR-014). Renewal and configuration come with the lease work (#19).</summary>
    public static readonly TimeSpan LockLease = TimeSpan.FromHours(4);
    const int MaxListedFailures = 50;

    /// <summary>Alerts that could not be raised. They never block the lock or the check run.</summary>
    public List<string> AlertFailures { get; } = [];

    public async Task<bool> Report(Target target, CheckRun check, CancellationToken ct)
    {
        var repo = target.Repo;
        if (check.Status == "completed" || !long.TryParse(check.ExternalId, out var runId)) return false;
        var outcome = Outcomes.Read(await github.Jobs(repo, runId, ct));
        if (outcome is null) return false;
        var runUrl = $"https://github.com/{repo}/actions/runs/{runId}";
        var summary = outcome.Description + $"\n\n[Target run]({runUrl})";
        if (outcome.Conclusion is "success" or "failure")
        {
            var reports = await github.Reports(repo, runId, ct);
            if (outcome.Conclusion == "failure")
            {
                var locks = await Locks(repo, ct);
                var lockIssue = locks.FirstOrDefault() ?? await OpenLock(target, check, reports, runUrl, ct);
                summary += $"\n\nLock issue: {lockIssue.Url}\n\n" + FailureList(reports);
            }
            else
            {
                foreach (var open in await Locks(repo, ct))
                {
                    await github.Comment(repo, open.Number,
                        $"Tests passed on {Commit(repo, check.Sha)} ([target run]({runUrl})), so `main` is green again. Closing the lock." +
                        $"\n\n<!-- main-watcher check={check.Id} -->", ct);
                    await github.Close(repo, open.Number, ct);
                }
                if (reports.Failures.Count > 0) summary += "\n\nWarning: CTRF reports failures despite a successful test step.";
            }
        }
        // GitHub caps check output at 65535 bytes. Conservatively bound UTF-16 length.
        if (summary.Length > 15000) summary = summary[..15000] + "\n\nOutput truncated; see target run.";
        await github.Complete(repo, check.Id, outcome.Conclusion, summary, ct);
        return true;
    }

    /// <summary>Open <c>main-broken</c> issues authored by the App, oldest first. Anyone else's issue is not a lock.</summary>
    async Task<Issue[]> Locks(string repo, CancellationToken ct) =>
        (await github.OpenIssues(repo, LockLabel, ct))
        .Where(i => i.Author == botLogin && i.AuthorType == "Bot").OrderBy(i => i.Number).ToArray();

    async Task<Issue> OpenLock(Target target, CheckRun check, CtrfResult reports, string runUrl, CancellationToken ct)
    {
        IReadOnlyList<string> mentions = target.Notify.Length > 0 ? target.Notify.Select(Mentions.Normalize).ToArray() : await CodeOwners(target.Repo, ct);
        var leaseUntil = (clock ?? (() => DateTimeOffset.UtcNow))().Add(LockLease).UtcDateTime;
        var body = (mentions.Count > 0 ? string.Join(" ", mentions) + "\n\n" : "")
            + $"Main Watcher tests failed on `main` at {Commit(target.Repo, check.Sha)}.\n\n"
            + "**Failing tests**\n\n" + FailureList(reports) + "\n\n"
            + $"[Target run]({runUrl})\n\n"
            + $"While this issue is open, the merge queue accepts only pull requests labelled `fixes-main`. "
            + "A green Main Watcher run on `main` closes it. Closing it by hand overrides the lock.\n\n"
            + $"<!-- main-watcher first_red={check.Sha} lease_until={leaseUntil.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} "
            + $"reported_check={check.Id} reported_sha={check.Sha} -->";
        var issue = await github.CreateIssue(target.Repo, $"main is broken: tests failed on {Short(check.Sha)}", body, LockLabel, ct);
        if (mentions.Count == 0)
            await Alert($"Lock issues on {target.Repo} mention nobody",
                $"Lock {issue.Url} mentions nobody: `{target.Repo}` has no `notify` list in targets.yml and no `*` rule with owners in CODEOWNERS. "
                + "Configure `notify` for this target.", ct);
        return issue;
    }

    async Task<IReadOnlyList<string>> CodeOwners(string repo, CancellationToken ct)
    {
        // GitHub uses the first CODEOWNERS file it finds, in this order.
        foreach (var path in Mentions.CodeOwnersPaths)
            if (await github.File(repo, path, ct) is { } text) return Mentions.StarOwners(text);
        return [];
    }

    async Task Alert(string title, string body, CancellationToken ct)
    {
        if (alerts is null) { AlertFailures.Add($"{title}: no alert sink configured"); return; }
        try { await alerts.Raise(title, body, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested) { AlertFailures.Add($"{title}: {e.Message}"); }
    }

    static string FailureList(CtrfResult reports)
    {
        if (!reports.Known || reports.Failures.Count == 0) return "failing tests unknown";
        var lines = reports.Failures.Take(MaxListedFailures).Select(f => $"- {Escape(f.Name)} ({Escape(f.Suite)}): {Escape(f.Message)}");
        var more = reports.Failures.Count - MaxListedFailures;
        return string.Join("\n", lines) + (more > 0 ? $"\n\n…and {more} more; see the target run." : "");
    }

    static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
    static string Commit(string repo, string sha) => $"[`{Short(sha)}`](https://github.com/{repo}/commit/{sha})";

    // HTML-encoding also stops test output from opening a hidden marker; &#64; stops it mentioning anyone.
    static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text)
        .Replace("\r", " ").Replace("\n", " ").Replace("`", "\\`").Replace("*", "\\*").Replace("[", "\\[").Replace("@", "&#64;");
}
