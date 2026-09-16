namespace MainWatcher.Core;

/// <summary>De-duplicated <c>watcher-infra</c> alerts in the watcher repo (ADR-012).</summary>
public sealed class Alerts(IGitHubGateway github, string repo)
{
    public const string Label = "watcher-infra";

    /// <summary>Opens an alert, or comments on the open alert with the same title.</summary>
    public async Task Raise(string title, string body, CancellationToken ct)
    {
        var open = (await github.OpenIssues(repo, Label, ct)).Where(i => i.Title == title).MinBy(i => i.Number);
        if (open is null) await github.CreateIssue(repo, title, body, Label, ct);
        else await github.Comment(repo, open.Number, body, ct);
    }
}
