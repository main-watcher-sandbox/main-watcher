namespace MainWatcher.Core;

/// <summary>De-duplicated <c>watcher-infra</c> alerts in the watcher repo (ADR-012).</summary>
public sealed class Alerts(IGitHubGateway github, string repo, Func<DateTimeOffset>? now = null)
{
    public const string Label = "watcher-infra";

    /// <summary>The prefix of the labels a <c>shared</c> alert claims a window with.</summary>
    public const string ClaimPrefix = "mw-claim-";

    /// <summary>What a claim label's description says before the time it was created.</summary>
    public const string ClaimedAt = "Main Watcher: this alert has been raised at ";

    /// <summary>How long a claim label is kept before the next winner deletes it.</summary>
    public static readonly TimeSpan ClaimKept = TimeSpan.FromDays(1);

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
    /// A <paramref name="shared"/> alert is one that cycles for different targets raise at the same moment, such as the
    /// installation's API budget: each checks and then writes, so two can both find nothing and both write (PR #62 review).
    /// Its title and key are therefore claimed first, by creating a label named after them. A label name is unique in a
    /// repository, so of any number of cycles racing, GitHub lets exactly one create it, and only that one writes: no
    /// duplicate issue and no duplicate comment is ever created, and so none is ever notified. A claim whose write then fails
    /// is given back, so the next cycle raises the alert.
    /// </para>
    /// </summary>
    public async Task Raise(string title, string body, CancellationToken ct, string? key = null, bool shared = false)
    {
        var open = (await github.OpenIssues(repo, Label, ct)).Where(i => i.Title == title).MinBy(i => i.Number) ?? Recent(title);
        if (key is not null)
        {
            if (open is not null && ((open.Body ?? "").Contains(key, StringComparison.Ordinal)
                || (await github.Comments(repo, open.Number, null, ct)).Any(c => c.Body.Contains(key, StringComparison.Ordinal)))) return;
            body += "\n\n" + key;
        }
        var claim = shared ? await Claim(title, key, ct) : null;
        if (shared && claim is null) return;
        try
        {
            if (open is null)
            {
                var alert = await github.CreateIssue(repo, title, body, Label, ct);
                lock (opened) opened.Add((alert, clock()));
            }
            else await github.Comment(repo, open.Number, body, ct);
        }
        catch
        {
            // The claim stands for an alert that was written. This one was not, so it is given back for the next cycle.
            if (claim is not null) await github.DeleteLabel(repo, claim, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Claims this title and key by creating the label that names them, and returns it; null when another cycle holds the
    /// claim already. The winner also deletes the claims older than <see cref="ClaimKept"/>, so the labels do not pile up.
    /// </summary>
    async Task<string?> Claim(string title, string? key, CancellationToken ct)
    {
        var at = clock();
        // The name is the alert and its window, and nothing else. It carried the claiming cycle's own minute until the PR #62
        // review: two cycles either side of a minute boundary then claimed different labels for the same window, and both
        // wrote. When the claim was made is kept in the description instead, which is all the cleanup below needs.
        var name = ClaimPrefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{title}\n{key}")))[..16].ToLowerInvariant();
        if (!await github.Claim(repo, name, $"{ClaimedAt}{Markers.Stamp(at)}. Deleted after {ClaimKept.TotalHours:0} h.", ct))
            return null;
        foreach (var old in await github.RepoLabels(repo, ClaimPrefix, ct))
            if (old.Name != name && Claimed(old) is { } made && at - made > ClaimKept) await github.DeleteLabel(repo, old.Name, ct);
        return name;
    }

    /// <summary>When a claim was made, from its description; null when the description does not say, so it is left alone.</summary>
    static DateTimeOffset? Claimed(RepoLabel label) => label.Description is { } said
        && said.StartsWith(ClaimedAt, StringComparison.Ordinal)
        ? Markers.Read(said[ClaimedAt.Length..].Split('.')[0]) : null;

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
