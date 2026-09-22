using MainWatcher.Core;

namespace MainWatcher.Worker;

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
        if (RateLimit.Read(response) is { } limit)
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
}
