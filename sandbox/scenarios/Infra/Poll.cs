namespace MainWatcher.Scenarios.Infra;

/// <summary>A scenario's expectation that did not hold. The message says what was expected and what was seen.</summary>
public sealed class ScenarioFailure(string message) : Exception(message);

/// <summary>Waiting on GitHub: every scenario step that depends on something asynchronous polls through here.</summary>
public static class Poll
{
    /// <summary>The usual pause between two looks. Scenarios run side by side, so it is kept gentle on the API budget.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Asks <paramref name="probe"/> until it answers something other than null, and returns that. Past
    /// <paramref name="timeout"/>, fails the scenario naming <paramref name="what"/>. A GitHub error is retried until then,
    /// because a flaky call must not decide a scenario.
    /// </summary>
    public static async Task<T> Until<T>(string what, TimeSpan timeout, Func<Task<T?>> probe, CancellationToken ct,
        TimeSpan? interval = null) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (true)
        {
            try
            {
                if (await probe() is { } found) return found;
                last = null;
            }
            catch (GitHubException e) { last = e; }
            if (DateTimeOffset.UtcNow >= deadline)
                throw new ScenarioFailure($"Timed out after {timeout.TotalMinutes:0.#} min waiting for {what}"
                    + (last is null ? "." : $"; the last look failed: {last.Message}"));
            await Task.Delay(interval ?? Interval, ct);
        }
    }

    /// <summary><see cref="Until{T}"/> for a value, such as a time.</summary>
    public static async Task<T> Value<T>(string what, TimeSpan timeout, Func<Task<T?>> probe, CancellationToken ct,
        TimeSpan? interval = null) where T : struct =>
        (await Until(what, timeout, async () => await probe() is { } found ? new Box<T>(found) : null, ct, interval)).Item;

    sealed record Box<T>(T Item);

    /// <summary><see cref="Until{T}"/> for a condition.</summary>
    public static Task True(string what, TimeSpan timeout, Func<Task<bool>> probe, CancellationToken ct, TimeSpan? interval = null) =>
        Until(what, timeout, async () => await probe() ? "" : null, ct, interval);

    /// <summary>Waits until a moment, such as a lease running out, logging nothing.</summary>
    public static async Task Till(DateTimeOffset moment, CancellationToken ct)
    {
        var wait = moment - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }
}
