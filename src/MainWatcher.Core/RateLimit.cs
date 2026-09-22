using System.Globalization;

namespace MainWatcher.Core;

/// <summary>
/// What GitHub's rate-limit headers said on one response: how much of <paramref name="Resource"/>'s budget is left, and when it
/// resets. Each installation has its own budget, so the worker and the watcher each keep the lowest they have seen (R-13).
/// </summary>
public sealed record RateLimit(string Resource, int Remaining, int Limit, DateTimeOffset? Reset)
{
    /// <summary>The share of the budget still available, 0 to 1. A response without a limit counts as a full budget.</summary>
    public double Left => Limit > 0 ? (double)Remaining / Limit : 1;

    /// <summary>"N of M requests left, refilled at HH:mm:ssZ", as a cycle logs it.</summary>
    public override string ToString() => $"{Remaining} of {Limit} requests left"
        + (Reset is { } reset ? $", refilled at {reset.UtcDateTime:HH:mm:ss}Z" : "");

    /// <summary>The rate limit a response reported, or null when it carried none.</summary>
    public static RateLimit? Read(HttpResponseMessage response)
    {
        // Every rate-limited response carries all three; a cached or non-API response carries none.
        if (Header(response, "x-ratelimit-remaining") is not { } remaining || Header(response, "x-ratelimit-limit") is not { } limit
            || !int.TryParse(remaining, NumberStyles.None, CultureInfo.InvariantCulture, out var left)
            || !int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var budget) || budget == 0) return null;
        return new(Header(response, "x-ratelimit-resource") ?? "core", left, budget,
            long.TryParse(Header(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset)
                ? DateTimeOffset.FromUnixTimeSeconds(reset) : null);
    }

    /// <summary>
    /// The lower of two readings, where a reading from a later window replaces one from an earlier window whatever its count:
    /// a budget that has refilled is not low any more.
    /// </summary>
    public static RateLimit? Lower(RateLimit? seen, RateLimit? next)
    {
        if (next is null) return seen;
        if (seen is null || next.Reset > seen.Reset + TimeSpan.FromMinutes(1)) return next;
        return next.Left < seen.Left ? next : seen;
    }

    /// <summary>
    /// Whether a refused response was GitHub's rate limit rather than a permission: a 429, or a 403 with the budget spent, a
    /// <c>retry-after</c>, or a message naming a rate limit. GitHub documents <c>retry-after</c> as optional on a secondary
    /// limit, which leaves the primary budget as it was, so its message is what marks it (PR #62 review).
    /// </summary>
    /// <param name="message">GitHub's error message, when the body has been read; the headers alone decide without it.</param>
    public static bool Refused(HttpResponseMessage response, string? message = null) =>
        response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
        || response.StatusCode == System.Net.HttpStatusCode.Forbidden
        && (response.Headers.RetryAfter is not null || Header(response, "x-ratelimit-remaining") == "0"
            || message?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true);

    static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
