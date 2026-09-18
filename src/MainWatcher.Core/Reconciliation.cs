namespace MainWatcher.Core;

/// <summary>
/// The rules reconciliation is built from (ADR-008, ADR-015): which merges fall inside a lock's window, whether the pull
/// request carried <c>fixes-main</c> at the moment it merged, and the markers that make each report happen exactly once.
/// <para>
/// A lock's window runs from the issue's creation to its closure, or to now while it is open, and it does not end when the
/// issue does: a human can close a lock before the watcher has looked at a merge made during it, and no run would ever look
/// again. So the cursor lives in the issue, open or closed, and <see cref="Complete"/> is written only once the whole window
/// has been checked.
/// </para>
/// </summary>
public static class Reconciliation
{
    /// <summary>The marker field holding how far into this lock's window the activity has been read.</summary>
    public const string Cursor = "last_reconciled";

    /// <summary>The marker field saying the whole window has been checked and every report made.</summary>
    public const string Complete = "reconciled";

    /// <summary>The only value <see cref="Complete"/> takes.</summary>
    public const string CompleteValue = "complete";

    /// <summary>The label that lets a pull request merge while <c>main</c> is locked (ADR-002).</summary>
    public const string FixLabel = "fixes-main";

    /// <summary>The section of the lock issue's body that lists what merged during it.</summary>
    public const string Heading = "**Merged while locked**";

    /// <summary>The activity types that put someone else's code on <c>main</c> (ADR-008 point 2).</summary>
    public static readonly string[] MergeActivity = ["pr_merge", "merge_queue_merge"];

    /// <summary>
    /// How long a merge may stay unresolved, or a closed lock unreconciled, before "reconciliation failing" is raised
    /// (ADR-015 point 7).
    /// </summary>
    public static readonly TimeSpan FailingAfter = TimeSpan.FromHours(24);

    /// <summary>Whether this lock's whole window has been checked, so no run need look at it again.</summary>
    public static bool IsComplete(string? body) => Markers.Field(body, Complete) == CompleteValue;

    static readonly System.Text.RegularExpressions.Regex MergeSubject = new(@"^Merge pull request #(?<number>\d+) from ");
    static readonly System.Text.RegularExpressions.Regex SquashSubject = new(@"\(#(?<number>\d+)\)$");

    /// <summary>
    /// The pull request a commit subject names, from the merge and squash subjects GitHub writes, or null when it names none.
    /// The gate matches the same two shapes when it works out which pull requests a merge group holds.
    /// </summary>
    public static int? PullOf(string subject) =>
        new[] { MergeSubject, SquashSubject }.Select(p => p.Match(subject)).FirstOrDefault(m => m.Success) is { } match
            && int.TryParse(match.Groups["number"].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>
    /// The pull requests one activity entry merged, oldest first, each dated by the commit that names it. A pull request
    /// merges once, so the earliest naming commit is its merge; the commits it brought with it name nothing.
    /// </summary>
    public static IReadOnlyList<(int Pull, DateTimeOffset MergedAt)> Pulls(IEnumerable<MergedCommit> commits) =>
        commits.Where(c => c.Pull is not null).GroupBy(c => c.Pull!.Value)
            .Select(g => (Pull: g.Key, MergedAt: g.Min(c => c.At)))
            .OrderBy(p => p.MergedAt).ThenBy(p => p.Pull).ToArray();

    /// <summary>The timeline event that dates a pull request's merge.</summary>
    public const string Merged = "merged";

    /// <summary>
    /// When the pull request merged, from its own timeline, which is what ADR-015 judges its labels at. Null when the
    /// timeline holds no merge, and the merge commit's own date is used instead: the merge-queue commit is built before the
    /// group merges, so the two differ by however long the queue took.
    /// </summary>
    public static DateTimeOffset? MergedAt(IEnumerable<PullEvent> events) =>
        events.Where(e => e.Name == Merged).Select(e => (DateTimeOffset?)e.At).LastOrDefault();

    /// <summary>
    /// Whether <paramref name="pull"/> carried <see cref="FixLabel"/> when it merged, from its label events
    /// (ADR-015 point 8). Events are replayed in the order GitHub returns them, oldest first, and one stamped in the same
    /// second as the merge counts as before it, so a label added in that second counts as present.
    /// </summary>
    public static bool WasFix(IEnumerable<PullEvent> events, DateTimeOffset mergedAt) =>
        events.Where(e => e.Label == FixLabel && e.At <= mergedAt)
            .Select(e => (bool?)(e.Name == "labeled")).LastOrDefault() == true;

    /// <summary>
    /// The hidden key that marks one merge reported. It names the pull request, not the activity entry, because the same
    /// merge is seen through a body row, a comment and an alert, and each must be written at most once.
    /// </summary>
    public static string Key(int pull) => $"<!-- main-watcher merged_while_locked pr={pull} -->";

    /// <summary>
    /// The hidden key for a merge no commit subject attributes to a pull request, which is keyed by the commit it left on
    /// <c>main</c> instead. Such a merge is reported rather than passed over: nothing says it carried <see cref="FixLabel"/>,
    /// and NFR-4 is about what landed, not about what could be named.
    /// </summary>
    public static string Key(string sha) => $"<!-- main-watcher merged_while_locked commit={sha} -->";

    /// <summary>
    /// The hidden key of the note saying the activity read did not reach back to the window's start, so the note is written
    /// at most once per lock however many cycles look at it.
    /// </summary>
    public const string Truncated = "<!-- main-watcher merged_while_locked truncated -->";

    /// <summary>
    /// Adds <paramref name="rows"/> to the body's "Merged while locked" section, creating it when it is missing. The section
    /// is kept immediately before the hidden marker, which <see cref="Markers.Set"/> then edits in place, so the rows and the
    /// cursor can be written together.
    /// </summary>
    public static string Append(string? body, IEnumerable<string> rows)
    {
        var text = body ?? "";
        var added = string.Join("\n", rows);
        if (added.Length == 0) return text;
        var section = text.Contains(Heading, StringComparison.Ordinal) ? "\n" + added : $"\n\n{Heading}\n\n{added}";
        var marker = text.LastIndexOf("<!-- main-watcher", StringComparison.Ordinal);
        return marker < 0 ? text + section : text[..marker].TrimEnd('\n') + section + "\n\n" + text[marker..];
    }
}
