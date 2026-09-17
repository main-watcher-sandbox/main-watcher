namespace MainWatcher.Core;

/// <summary>De-duplicated <c>watcher-infra</c> alerts in the watcher repo (ADR-012).</summary>
public sealed class Alerts(IGitHubGateway github, string repo)
{
    public const string Label = "watcher-infra";

    /// <summary>
    /// Opens an alert, or comments on the open alert with the same title. With a <paramref name="key"/>, such as a hidden
    /// marker naming a check run, the alert is skipped when the open alert's body or a comment already holds that key, so a
    /// replayed report does not repeat it.
    /// </summary>
    public async Task Raise(string title, string body, CancellationToken ct, string? key = null)
    {
        var open = (await github.OpenIssues(repo, Label, ct)).Where(i => i.Title == title).MinBy(i => i.Number);
        if (key is not null)
        {
            if (open is not null && ((open.Body ?? "").Contains(key, StringComparison.Ordinal)
                || (await github.Comments(repo, open.Number, null, ct)).Any(c => c.Body.Contains(key, StringComparison.Ordinal)))) return;
            body += "\n\n" + key;
        }
        if (open is null) await github.CreateIssue(repo, title, body, Label, ct);
        else await github.Comment(repo, open.Number, body, ct);
    }
}
