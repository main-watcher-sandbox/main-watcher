using System.Globalization;

namespace MainWatcher.Worker;

/// <summary>
/// What GitHub's rate-limit headers said on one response: how much of <paramref name="Resource"/>'s budget is left, and when it
/// resets. Each installation has its own budget, so the worker keeps the lowest it has seen (R-13).
/// </summary>
public sealed record RateLimit(string Resource, int Remaining, int Limit, DateTimeOffset? Reset)
{
    /// <summary>The share of the budget still available, 0 to 1. A response without a limit counts as a full budget.</summary>
    public double Left => Limit > 0 ? (double)Remaining / Limit : 1;
}

/// <summary>What the worker's GitHub access says about its own health, for <see cref="WorkerAlerts"/>.</summary>
public interface IAccessHealth
{
    /// <summary>
    /// Credentials GitHub is refusing now: "mw-observer on owner/repo" to the status it answered. A credential that mints a
    /// token again is removed, so the alert clears by itself.
    /// </summary>
    IReadOnlyDictionary<string, string> TokenFailures { get; }

    /// <summary>The lowest remaining rate limit seen since the previous call, and forgets it. Null when GitHub reported none.</summary>
    RateLimit? TakeLowestRateLimit();
}

/// <summary>
/// Records the rate-limit headers of every GitHub response the worker receives. One instance sits under all of its clients, so
/// the lowest budget of any App and installation is the one the worker reports (R-13).
/// </summary>
public sealed class RateLimitWatch : DelegatingHandler
{
    readonly Lock gate = new();
    RateLimit? lowest;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (Read(response) is { } limit)
            lock (gate)
                if (lowest is null || limit.Left < lowest.Left) lowest = limit;
        return response;
    }

    public RateLimit? Take()
    {
        lock (gate)
        {
            var seen = lowest;
            lowest = null;
            return seen;
        }
    }

    static RateLimit? Read(HttpResponseMessage response)
    {
        // Every rate-limited response carries all three; a cached or non-API response carries none.
        if (Header(response, "x-ratelimit-remaining") is not { } remaining || Header(response, "x-ratelimit-limit") is not { } limit
            || !int.TryParse(remaining, NumberStyles.None, CultureInfo.InvariantCulture, out var left)
            || !int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var budget) || budget == 0) return null;
        return new(Header(response, "x-ratelimit-resource") ?? "core", left, budget,
            long.TryParse(Header(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset)
                ? DateTimeOffset.FromUnixTimeSeconds(reset) : null);
    }

    static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
