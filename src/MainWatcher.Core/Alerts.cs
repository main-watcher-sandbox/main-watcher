namespace MainWatcher.Core;

/// <summary>De-duplicated <c>watcher-infra</c> alerts in the watcher repo (ADR-012).</summary>
public sealed class Alerts(IGitHubGateway github, string repo, Func<DateTimeOffset>? now = null)
{
    public const string Label = "watcher-infra";

    /// <summary>
    /// How long an alert this instance opened is trusted without the issue list. The list does not hold an issue created a
    /// second or two earlier: a cycle that reported two merges 2 s apart opened two "Merged while locked" alerts, not one
    /// alert and a comment (scenario suite, #25). Longer than the list's lag, and short, because an alert a person has
    /// closed since must not take comments.
    /// </summary>
    public static readonly TimeSpan Remembered = TimeSpan.FromMinutes(2);

    readonly Func<DateTimeOffset> clock = now ?? (() => DateTimeOffset.UtcNow);
    readonly List<(Issue Alert, DateTimeOffset Opened)> opened = [];

    /// <summary>
    /// Opens an alert, or comments on the open alert with the same title. With a <paramref name="key"/>, such as a hidden
    /// marker naming a check run, the alert is skipped when the open alert's body or a comment already holds that key, so a
    /// replayed report does not repeat it.
    /// </summary>
    public async Task Raise(string title, string body, CancellationToken ct, string? key = null)
    {
        var open = (await github.OpenIssues(repo, Label, ct)).Where(i => i.Title == title).MinBy(i => i.Number) ?? Recent(title);
        if (key is not null)
        {
            if (open is not null && ((open.Body ?? "").Contains(key, StringComparison.Ordinal)
                || (await github.Comments(repo, open.Number, null, ct)).Any(c => c.Body.Contains(key, StringComparison.Ordinal)))) return;
            body += "\n\n" + key;
        }
        if (open is null)
        {
            var alert = await github.CreateIssue(repo, title, body, Label, ct);
            lock (opened) opened.Add((alert, clock()));
        }
        else await github.Comment(repo, open.Number, body, ct);
    }

    /// <summary>The alert with this title that this instance opened within <see cref="Remembered"/>, if any.</summary>
    Issue? Recent(string title)
    {
        var since = clock() - Remembered;
        lock (opened)
        {
            opened.RemoveAll(o => o.Opened < since);
            return opened.Where(o => o.Alert.Title == title).Select(o => o.Alert).MinBy(a => a.Number);
        }
    }
}
