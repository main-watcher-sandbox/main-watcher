using System.Globalization;
using System.Text.RegularExpressions;

namespace MainWatcher.Gate;

/// <summary>
/// The lease on a lock issue (ADR-014): <c>lease_until=&lt;UTC timestamp&gt;</c> in the hidden
/// <c>&lt;!-- main-watcher … --&gt;</c> marker. The gate enforces a lock only while its lease is valid.
/// </summary>
public static class LockLease
{
    /// <summary>A lease further ahead than this is treated as invalid, so a bad marker cannot make a lock unbounded.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromHours(24);

    static readonly Regex Marker = new(@"<!--\s*main-watcher\s(?<fields>.*?)-->", RegexOptions.Singleline);

    static readonly string[] TimestampFormats = ["yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.FFFFFFFZ"];

    /// <summary>
    /// Reads <c>lease_until</c> from an issue body. Returns null when the field is missing, appears more than once, or is not
    /// a UTC timestamp such as <c>2026-09-16T18:00:00Z</c>.
    /// <para>
    /// Every marker in the body is looked at, not only a single one. A lock issue carries more than one as soon as anything is
    /// reported on it: each "Merged while locked" row ends in its own hidden key (ADR-015), and requiring exactly one marker
    /// made the gate read "no readable lease" from then on, so it failed open for the rest of that lock's life. Ambiguity is
    /// still refused, but it is ambiguity about the <b>lease</b>: two <c>lease_until</c> fields, wherever they are written.
    /// </para>
    /// </summary>
    public static DateTimeOffset? ReadLeaseUntil(string? issueBody)
    {
        var values = Marker.Matches(issueBody ?? "")
            .SelectMany(marker => marker.Groups["fields"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(field => field.StartsWith("lease_until=", StringComparison.Ordinal))
            .Select(field => field["lease_until=".Length..])
            .ToList();
        if (values.Count != 1)
            return null;

        return DateTimeOffset.TryParseExact(values[0], TimestampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var leaseUntil)
            ? leaseUntil
            : null;
    }

    /// <summary>A lease is valid when it is in the future and at most <see cref="MaxAhead"/> ahead.</summary>
    public static bool IsValid(DateTimeOffset? leaseUntil, DateTimeOffset now) =>
        leaseUntil is { } lease && lease > now && lease <= now + MaxAhead;
}
