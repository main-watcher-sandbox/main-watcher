namespace MainWatcher.Core;

/// <summary>
/// The lease that keeps an open lock enforced (ADR-014). A lock issue says nothing about whether anyone still maintains it,
/// so the gate enforces one only while its <c>lease_until</c> marker is in the future; a watcher that stops therefore blocks
/// ordinary merges for at most <c>lock_lease</c>, not forever (NFR-3).
/// <para>
/// This is the one rule the Planner, which renews the lease, and the trigger worker, which flags a lock whose lease is an hour
/// old so a cycle runs at all, both read (TS-U5, TS-S7). The gate reads the same marker through its own copy
/// (<c>MainWatcher.Gate.LockLease</c>), because it shares no code with the watcher.
/// </para>
/// </summary>
public static class Lease
{
    /// <summary>The issue-body marker field holding when this lock stops being enforced.</summary>
    public const string Until = "lease_until";

    /// <summary>The marker field recording a lease that had already run out when it was renewed: <c>&lt;expiry&gt;..&lt;renewal&gt;</c>.</summary>
    public const string Lapsed = "lapsed";

    /// <summary>The marker field recording that a lapse's comment and alert were posted, by the renewal time they name.</summary>
    public const string Reported = "lapse_reported";

    /// <summary>The ADR-016 marker field: merge groups queued before this time still need their gate re-run (#21).</summary>
    public const string SweepRequired = "sweep_required";

    /// <summary><c>lock_lease</c>, as the <c>targets.yml</c> default sets it for every target.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromHours(4);

    /// <summary>
    /// How old a lease may get before the worker asks for a renewal. It is far shorter than <see cref="Default"/> so that
    /// renewal never depends on GitHub's unreliable schedule (C-7), and a lapse needs several missed cycles.
    /// </summary>
    public static readonly TimeSpan RenewAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// When this lock's lease next needs renewing: <see cref="RenewAfter"/> after it was last renewed, or half of
    /// <c>lock_lease</c> where that is sooner.
    /// <para>
    /// The half only bites below a two-hour <c>lock_lease</c>, which in practice means the sandbox's ten minutes. It exists
    /// because asking for a renewal a fixed hour after the last one would, for a lease shorter than that hour, ask only once
    /// the lease had already expired: every renewal would then follow a window in which the gate had stopped enforcing a lock
    /// nobody had abandoned. Renewing halfway through leaves the same margin proportionally that four hours and one hour do.
    /// </para>
    /// <para>
    /// Null when the marker is missing or unreadable, and when it is further ahead than a renewal could have set it, which a
    /// hand-edited or faulty marker is: each needs renewing now, and none of them dates the work.
    /// </para>
    /// </summary>
    public static DateTimeOffset? Due(string? body, TimeSpan lockLease, DateTimeOffset now) =>
        Markers.Time(body, Until) is { } until && until <= now + lockLease
            ? until - lockLease + (RenewAfter < lockLease / 2 ? RenewAfter : lockLease / 2) : null;

    /// <summary>Whether the lease wants renewing now. An unreadable one always does, and an expired one always has.</summary>
    public static bool RenewalDue(string? body, TimeSpan lockLease, DateTimeOffset now) =>
        Due(body, lockLease, now) is not { } due || due <= now;

    /// <summary>
    /// How many unreported windows <see cref="Lapsed"/> carries before the oldest crowd out the rest. Reaching it needs that
    /// many consecutive cycles that each renewed a lapsed lease and then died before reporting it, so it exists only to bound
    /// the marker. The oldest are kept, because they are the ones reconciliation has to look back at.
    /// </summary>
    public const int MaxWindows = 20;

    /// <summary>
    /// The lapses whose comment and alert are still owed, oldest first. They are written after the renewal, so a crash between
    /// them leaves the window owed and a later cycle posts it (ADR-014 point 4).
    /// <para>
    /// There can be more than one. A lease that is renewed and then lapses again before the first report was made must not
    /// lose the first window, so a renewal keeps every owed window and appends its own; a window is owed until
    /// <see cref="Reported"/> reaches its renewal time.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset From, DateTimeOffset At)> Unreported(string? body) =>
        Markers.Time(body, Reported) is { } reported
            ? Windows(body).Where(w => w.At > reported).ToArray() : Windows(body);

    /// <summary>The lapse windows <see cref="Lapsed"/> holds, oldest first; unreadable ones are skipped.</summary>
    public static IReadOnlyList<(DateTimeOffset From, DateTimeOffset At)> Windows(string? body) =>
        (Markers.Field(body, Lapsed) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Split("..") is [var from, var at] && Markers.Read(from) is { } start && Markers.Read(at) is { } end
                ? (From: start, At: end) : default)
            .Where(w => w != default).ToArray();

    /// <summary>The <see cref="Lapsed"/> value holding <paramref name="windows"/>, bounded by <see cref="MaxWindows"/>.</summary>
    public static string Field(IEnumerable<(DateTimeOffset From, DateTimeOffset At)> windows) =>
        string.Join(",", windows.Take(MaxWindows).Select(w => $"{Markers.Stamp(w.From)}..{Markers.Stamp(w.At)}"));
}
