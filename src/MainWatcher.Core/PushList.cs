namespace MainWatcher.Core;

/// <summary>How the push list was bounded (FR-3, ADR-003).</summary>
public enum PushSource
{
    /// <summary>Every push after the one that made the last green commit the head of <c>main</c>.</summary>
    SinceGreen,
    /// <summary>The green commit is not in the walked history of <c>main</c>: activity after its check run started.</summary>
    AfterGreenCheck,
    /// <summary>No green run was found: the newest <see cref="PushList.Limit"/> pushes.</summary>
    LastPushes,
    /// <summary>Reading history, check runs or activity failed.</summary>
    Unavailable
}

public sealed record ListedPush(Push Push, int? Commits);

/// <summary>What could have broken <c>main</c>. <see cref="Green"/> is the newest green check run found, if any.</summary>
public sealed record PushListResult(PushSource Source, CheckRun? Green, IReadOnlyList<ListedPush> Pushes, bool Truncated);

/// <summary>Finds the newest green check run on <c>main</c> and lists repository activity since it (FR-3).</summary>
public static class PushList
{
    /// <summary>Walk-back cap from ADR-003, also the activity cap.</summary>
    public const int Limit = 100;

    public static async Task<PushListResult> Collect(IGitHubGateway github, string repo, string failingSha, CancellationToken ct)
    {
        CheckRun? green = null;
        try
        {
            var history = await github.History(repo, failingSha, Limit, ct);
            foreach (var sha in history)
            {
                if (sha == failingSha) continue;
                if (await NewestGreen(github, repo, sha, ct) is { } found) { green = found; break; }
            }
            var pushes = await github.Pushes(repo, Limit, ct);
            PushSource source;
            IEnumerable<Push> listed;
            if (green is not null)
            {
                source = PushSource.SinceGreen;
                // The push that made the green commit the head is where its run's coverage ends.
                listed = pushes.TakeWhile(p => p.After != green.Sha);
            }
            else
            {
                // A force push can remove the green commit from history, but the activity that
                // once made it the head still names it.
                var walked = history.ToHashSet(StringComparer.Ordinal);
                foreach (var sha in pushes.Select(p => p.After).Where(s => s.Length > 0 && !s.All(c => c == '0')).Distinct())
                {
                    if (!walked.Add(sha)) continue;
                    if (await NewestGreen(github, repo, sha, ct) is { } found) { green = found; break; }
                }
                source = green is null ? PushSource.LastPushes : PushSource.AfterGreenCheck;
                listed = green is null ? pushes : pushes.Where(p => p.Timestamp > green.StartedAt);
            }
            var rows = listed.ToArray();
            var truncated = pushes.Count == Limit && rows.Length == pushes.Count;
            var counted = new List<ListedPush>();
            foreach (var push in rows) counted.Add(new(push, await Count(github, repo, push, ct)));
            return new(source, green, counted, truncated);
        }
        // The lock must still open: without a push list it links a comparison instead.
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new(PushSource.Unavailable, green, [], false);
        }
    }

    /// <summary>A commit is green when its newest check run succeeded (ADR-017).</summary>
    static async Task<CheckRun?> NewestGreen(IGitHubGateway github, string repo, string sha, CancellationToken ct)
    {
        var newest = (await github.CommitChecks(repo, sha, ct)).OrderByDescending(c => c.StartedAt).ThenByDescending(c => c.Id).FirstOrDefault();
        return newest is { Status: "completed", Conclusion: "success" } ? newest : null;
    }

    // A count that cannot be read is shown as unknown rather than dropping the push list.
    static async Task<int?> Count(IGitHubGateway github, string repo, Push push, CancellationToken ct)
    {
        try { return await github.CommitCount(repo, push.Before, push.After, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
    }
}
