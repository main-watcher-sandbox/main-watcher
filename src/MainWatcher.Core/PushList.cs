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

/// <summary>What could have broken <c>main</c>. <see cref="Green"/> is the newest green check run found, if any.
/// <see cref="CommitsChecked"/> is the walk-back length: commits whose check runs were read (ADR-003).
/// <see cref="Incomplete"/> means the activity read may not reach back to the start of the list.</summary>
public sealed record PushListResult(PushSource Source, CheckRun? Green, IReadOnlyList<ListedPush> Pushes, bool Incomplete, int CommitsChecked);

/// <summary>Finds the newest green check run on <c>main</c> and lists repository activity since it (FR-3).</summary>
public static class PushList
{
    /// <summary>Walk-back cap from ADR-003, also the activity cap.</summary>
    public const int Limit = 100;
    /// <summary>How far after its check run started a push to the green commit may be stamped and still count as the one it tested.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    public static async Task<PushListResult> Collect(IGitHubGateway github, string repo, string failingSha, CancellationToken ct)
    {
        CheckRun? green = null;
        var commitsChecked = 0;
        try
        {
            var history = await github.History(repo, failingSha, Limit, ct);
            foreach (var sha in history)
            {
                if (sha == failingSha) continue;
                if (await NewestGreen(github, repo, sha, () => commitsChecked++, ct) is { } found) { green = found; break; }
            }
            var pushes = await github.Pushes(repo, Limit, ct);
            PushSource source;
            IEnumerable<Push> listed;
            var incomplete = false;
            if (green is not null)
            {
                source = PushSource.SinceGreen;
                var boundary = GreenPush(pushes, green);
                // Without the green run's own push in the activity read, keep everything read, flagged as incomplete.
                incomplete = boundary is null;
                listed = pushes.Take(boundary ?? pushes.Count);
            }
            else
            {
                // A force push can remove the green commit from history, but the activity that
                // once made it the head still names it.
                var walked = history.ToHashSet(StringComparer.Ordinal);
                foreach (var sha in pushes.Select(p => p.After).Where(s => s.Length > 0 && !s.All(c => c == '0')).Distinct())
                {
                    if (!walked.Add(sha)) continue;
                    if (await NewestGreen(github, repo, sha, () => commitsChecked++, ct) is { } found) { green = found; break; }
                }
                source = green is null ? PushSource.LastPushes : PushSource.AfterGreenCheck;
                listed = green is null ? pushes : pushes.Where(p => p.Timestamp > green.StartedAt);
            }
            var rows = listed.ToArray();
            incomplete |= pushes.Count == Limit && rows.Length == pushes.Count;
            var counted = new List<ListedPush>();
            foreach (var push in rows) counted.Add(new(push, await Count(github, repo, push, ct)));
            return new(source, green, counted, incomplete, commitsChecked);
        }
        // The lock must still open: without a push list it links a comparison instead.
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new(PushSource.Unavailable, green, [], false, commitsChecked);
        }
    }

    /// <summary>
    /// The index, in newest-first <paramref name="pushes"/>, of the push that made the green commit the head its
    /// check run tested: the newest push to that commit at or before the run started. A later push back to the
    /// same commit (a rollback) is listed, not taken as the boundary. Failing that, the oldest push to it within
    /// <see cref="ClockSkew"/> after the start, as clocks may differ. Null when the activity read holds neither,
    /// for example when the push is older than the newest <see cref="Limit"/> entries.
    /// </summary>
    static int? GreenPush(IReadOnlyList<Push> pushes, CheckRun green)
    {
        int? skewed = null;
        for (var i = 0; i < pushes.Count; i++)
        {
            if (pushes[i].After != green.Sha) continue;
            if (pushes[i].Timestamp <= green.StartedAt) return i;
            if (pushes[i].Timestamp <= green.StartedAt + ClockSkew) skewed = i;
        }
        return skewed;
    }

    /// <summary>A commit is green when its newest check run succeeded (ADR-017).</summary>
    static async Task<CheckRun?> NewestGreen(IGitHubGateway github, string repo, string sha, Action counted, CancellationToken ct)
    {
        counted();
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

/// <summary>How many commits' check runs one push list read to find the last green run, and the source it settled on.</summary>
public sealed record WalkBack(long CheckId, int CommitsChecked, PushSource Source);
