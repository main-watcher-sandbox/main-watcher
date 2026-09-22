namespace MainWatcher.Core;

/// <summary>De-duplicated <c>watcher-infra</c> alerts in the watcher repo (ADR-012).</summary>
public sealed class Alerts(IGitHubGateway github, string repo, Func<DateTimeOffset>? now = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const string Label = "watcher-infra";

    /// <summary>
    /// How long the writer of a <c>shared</c> alert waits before it looks again for a copy another cycle wrote at the same
    /// time. Longer than the issue list's lag, which <see cref="Remembered"/> puts at a second or two.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

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
    /// <para>
    /// A <paramref name="shared"/> alert is one that cycles for different targets can raise at the same moment, such as the
    /// installation's API budget: each checks and then writes, so two can both find nothing and both write (PR #62 review).
    /// Its writer therefore waits <see cref="Settle"/> and tidies: every newer open copy is closed as a duplicate of the
    /// oldest, and every comment repeating a key an earlier one carries is deleted. Every writer tidies the same way, so
    /// whichever finishes last, one issue and one comment per key remain. The check before writing tidies too, so a copy
    /// left by a writer that stopped early is removed by the next.
    /// </para>
    /// </summary>
    public async Task Raise(string title, string body, CancellationToken ct, string? key = null, bool shared = false)
    {
        var open = (shared ? await Tidy(title, key, ct)
            : (await github.OpenIssues(repo, Label, ct)).Where(i => i.Title == title).MinBy(i => i.Number)) ?? Recent(title);
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
        if (!shared) return;
        await (delay ?? Task.Delay)(Settle, ct);
        await Tidy(title, key, ct);
    }

    /// <summary>
    /// The oldest open alert with this title, once every newer one is closed as its duplicate and every comment on it that
    /// repeats <paramref name="key"/> after its first appearance is deleted. Null when none is open.
    /// </summary>
    async Task<Issue?> Tidy(string title, string? key, CancellationToken ct)
    {
        var copies = (await github.OpenIssues(repo, Label, ct)).Where(i => i.Title == title).OrderBy(i => i.Number).ToArray();
        if (copies.Length == 0) return null;
        var canonical = copies[0];
        foreach (var copy in copies.Skip(1)) await github.Close(repo, copy.Number, "duplicate", canonical.Id, ct);
        if (key is null) return canonical;
        var keyed = (await github.Comments(repo, canonical.Number, null, ct))
            .Where(c => c.Body.Contains(key, StringComparison.Ordinal)).OrderBy(c => c.Id).ToArray();
        // The body counts as the first appearance, so then every comment carrying the key repeats it.
        foreach (var repeat in keyed.Skip((canonical.Body ?? "").Contains(key, StringComparison.Ordinal) ? 0 : 1))
            await github.DeleteComment(repo, repeat.Id, ct);
        return canonical;
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
