namespace MainWatcher.Gate.Tests;

// TS-U9: the gate enforces a lock only when lease_until is in the future and at most 24 h ahead.
public class LockLeaseTests
{
    static readonly DateTimeOffset Now = FakeGitHub.Now;

    [Theory]
    [InlineData("<!-- main-watcher lease_until=2026-09-16T16:00:00Z -->", "2026-09-16T16:00:00Z")]
    [InlineData("text\n<!-- main-watcher last_green=abc lease_until=2026-09-16T16:00:00.1234567Z sweep_required=2026-09-16T12:00:00Z -->\nmore", "2026-09-16T16:00:00.1234567Z")]
    [InlineData("<!--main-watcher\nlast_green=abc\nlease_until=2026-09-16T16:00:00Z\n-->", "2026-09-16T16:00:00Z")]
    // A lock carries a marker per reported merge as well as its own state (ADR-015). Before TS-S17 found it, the gate read
    // "no readable lease" from the first such report onwards, and so failed open for the rest of that lock's life.
    [InlineData("**Merged while locked**\n\n- [#46](url) merged without the label. <!-- main-watcher merged_while_locked pr=46 -->"
        + "\n\n<!-- main-watcher first_red=abc lease_until=2026-09-16T16:00:00Z queue_swept=2026-09-16T12:00:00Z -->", "2026-09-16T16:00:00Z")]
    public void Reads_the_lease_from_the_marker(string body, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), LockLease.ReadLeaseUntil(body));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A lock with no marker")]
    [InlineData("<!-- main-watcher last_green=abc -->")]
    [InlineData("lease_until=2026-09-16T16:00:00Z")]
    [InlineData("<!-- other lease_until=2026-09-16T16:00:00Z -->")]
    [InlineData("<!-- main-watcher lease_until= -->")]
    [InlineData("<!-- main-watcher lease_until=tomorrow -->")]
    [InlineData("<!-- main-watcher lease_until=2026-09-16T16:00:00 -->")]
    [InlineData("<!-- main-watcher lease_until=2026-09-16T16:00:00+02:00 -->")]
    [InlineData("<!-- main-watcher lease_until=2026-13-16T16:00:00Z -->")]
    [InlineData("<!-- main-watcher xlease_until=2026-09-16T16:00:00Z -->")]
    [InlineData("<!-- main-watcher lease_until=2026-09-16T16:00:00Z lease_until=2026-09-16T17:00:00Z -->")]
    [InlineData("<!-- main-watcher lease_until=2026-09-16T16:00:00Z --> <!-- main-watcher lease_until=2026-09-16T17:00:00Z -->")]
    public void A_missing_or_unreadable_lease_reads_as_none(string? body) =>
        Assert.Null(LockLease.ReadLeaseUntil(body));

    [Theory]
    [InlineData(1)]                 // one second ahead
    [InlineData(4 * 3600)]          // the default lock_lease
    [InlineData(24 * 3600)]         // exactly 24 h ahead
    public void A_lease_in_the_future_and_at_most_24_hours_ahead_is_valid(int secondsAhead) =>
        Assert.True(LockLease.IsValid(Now.AddSeconds(secondsAhead), Now));

    [Theory]
    [InlineData(0)]                 // expires now
    [InlineData(-1)]                // expired
    [InlineData(-7 * 24 * 3600)]    // long expired
    [InlineData(24 * 3600 + 1)]     // too distant
    [InlineData(365 * 24 * 3600)]   // far too distant
    public void An_expired_or_too_distant_lease_is_invalid(int secondsAhead) =>
        Assert.False(LockLease.IsValid(Now.AddSeconds(secondsAhead), Now));

    [Fact]
    public void No_lease_is_invalid() =>
        Assert.False(LockLease.IsValid(null, Now));
}
