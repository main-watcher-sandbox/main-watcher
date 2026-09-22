namespace MainWatcher.Core;

/// <summary>
/// The watcher's alerts about the <c>main-watcher</c> App installation's API budget (R-13, #60). Every target's cycles share
/// that one budget, so it is what bounds how many targets one installation can serve; the worker's own Apps have budgets of
/// their own and never see it run out. The alerts go through the watcher repo's workflow token, a separate budget, so they
/// can still be written when this one is spent.
/// </summary>
public static class InstallationBudget
{
    /// <summary>The share of the budget below which a cycle alerts, as the worker does for its own Apps.</summary>
    public const double Floor = 0.2;

    public const string LowTitle = "The main-watcher App's API budget is below 20%";
    public const string RefusedTitle = "The main-watcher App's API rate limit is refusing cycles";

    /// <summary>
    /// Raises the alert this cycle's budget calls for, if any: <see cref="RefusedTitle"/> when GitHub refused a request on its
    /// rate limit, otherwise <see cref="LowTitle"/> when less than <see cref="Floor"/> of the budget was left. Each is one
    /// issue, commented on at most once per budget window, however many cycles and targets see it.
    /// </summary>
    /// <returns>The title raised, or null when the budget needed no alert.</returns>
    public static async Task<string?> Judge(string repo, RateLimit? budget, string? refused, Alerts alerts, DateTimeOffset now,
        CancellationToken ct)
    {
        var refill = budget?.Reset is { } reset ? $" It refills at {reset.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC." : "";
        // One window per refill, so a lasting shortage is one comment an hour. GitHub has answered one sweep from two budgets
        // refilling seconds apart, so the minute, not the second, names the window.
        var key = $"<!-- main-watcher api-budget window={(budget?.Reset ?? now).UtcDateTime:yyyy-MM-ddTHH:mm} -->";
        const string Advice = "Every target's `watch.yml` cycles share this installation's hourly budget (R-13). Until it "
            + "refills, cycles fail on their first refused request, and the trigger worker asks for them again. To spend less, "
            + "watch fewer targets with this installation or raise their `poll_interval`; each cycle logs its requests by endpoint.";
        if (refused is not null)
        {
            await alerts.Raise(RefusedTitle, $"A cycle for `{repo}` was refused by GitHub's rate limit:\n\n> {refused}\n\n"
                + (budget is null ? "" : $"The lowest budget it saw was {budget}.") + refill + "\n\n" + Advice, ct, key);
            return RefusedTitle;
        }
        if (budget is null || budget.Left >= Floor) return null;
        await alerts.Raise(LowTitle, $"A cycle for `{repo}` left {budget.Remaining} of {budget.Limit} `{budget.Resource}` "
            + $"requests ({budget.Left * 100:0}%).{refill}\n\n{Advice}", ct, key);
        return LowTitle;
    }
}
