namespace MainWatcher.Core;

/// <summary>Reports the ADR-013 outcome with ADR-007 CTRF failure details.</summary>
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
