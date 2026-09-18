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
    /// When this lock's lease next needs renewing: the earlier of its first <see cref="RenewAfter"/> running out and the
    /// lease itself expiring, which is what a <c>lock_lease</c> shorter than <see cref="RenewAfter"/> reaches first.
    /// <para>
    /// Null when the marker is missing or unreadable, and when it is further ahead than a renewal could have set it, which a
    /// hand-edited or faulty marker is: each needs renewing now, and none of them dates the work.
    /// </para>
    /// </summary>
    public static DateTimeOffset? Due(string? body, TimeSpan lockLease, DateTimeOffset now) =>
        Markers.Time(body, Until) is { } until && until <= now + lockLease
            ? until - lockLease + (RenewAfter < lockLease ? RenewAfter : lockLease) : null;

    /// <summary>Whether the lease wants renewing now. An unreadable one always does, and an expired one always has.</summary>
    public static bool RenewalDue(string? body, TimeSpan lockLease, DateTimeOffset now) =>
        Due(body, lockLease, now) is not { } due || due <= now;

    /// <summary>
    /// The lapse a renewal recorded, when its comment and alert are still owed. They and the <see cref="Reported"/> marker are
    /// written after the renewal, so a crash between them leaves this owed and a later cycle posts it (ADR-014 point 4).
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset At)? Unreported(string? body) =>
        Window(body) is { } window && (Markers.Time(body, Reported) is not { } reported || reported < window.At)
            ? window : null;

    /// <summary>The lapse window <see cref="Lapsed"/> holds; null when it is missing or unreadable.</summary>
    public static (DateTimeOffset From, DateTimeOffset At)? Window(string? body) =>
        Markers.Field(body, Lapsed)?.Split("..") is [var from, var at]
            && Markers.Read(from) is { } start && Markers.Read(at) is { } end ? (start, end) : null;
}
