using System.Text.Json;
using System.Text.RegularExpressions;

namespace MainWatcher.Gate;

public enum GateOutcome { Pass, Fail }

/// <summary>
/// The gate's decision. <see cref="FailOpenReason"/> is set when the gate passed only because it
/// could not enforce a lock; the template then adds the <c>main-watcher/gate-fail-open</c> check run.
/// </summary>
public sealed record GateVerdict(GateOutcome Outcome, string Title, string Detail, string? FailOpenReason = null)
{
    public const string ApiError = "api-error";
    public const string LeaseExpired = "lease-expired";

    public static GateVerdict Pass(string title, string detail) => new(GateOutcome.Pass, title, detail);
    public static GateVerdict Fail(string title, string detail) => new(GateOutcome.Fail, title, detail);
    public static GateVerdict FailOpen(string reason, string title, string detail) => new(GateOutcome.Pass, title, detail, reason);
}

/// <summary>The fields of the triggering event the gate uses.</summary>
public sealed record GateEvent(string Name, string? BaseSha = null, string? HeadSha = null, string? HeadRef = null)
{
    public static GateEvent Read(string name, JsonElement payload) =>
        payload.TryGetProperty("merge_group", out var group) && group.ValueKind == JsonValueKind.Object
            ? new(name, String(group, "base_sha"), String(group, "head_sha"), String(group, "head_ref"))
            : new(name);

    static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Decides whether a merge group may merge (ADR-002, ADR-008, ADR-014). On <c>merge_group</c> it
/// fails while an open <c>main-broken</c> issue authored by the App has a valid lease, unless every
/// PR in the group is labelled <c>fixes-main</c>. Every other event passes. API errors and an
/// invalid lease fail open.
/// </summary>
public sealed class Gate(GitHubApi api, string repository, string botLogin = Gate.DefaultBotLogin)
{
    public const string DefaultBotLogin = "main-watcher[bot]";
    public const string LockLabel = "main-broken";
    public const string FixLabel = "fixes-main";
    public const string LockStatusUnknown = "LOCK STATUS UNKNOWN — failed open";
    public const string LockLeaseExpired = "LOCK LEASE EXPIRED — failed open";

    static readonly Regex QueueBranch = new(@"^gh-readonly-queue/(?<branch>.+)/pr-(?<number>\d+)-[0-9a-f]{40}$");
    static readonly Regex MergeCommitSubject = new(@"^Merge pull request #(?<number>\d+) from ");
    static readonly Regex SquashCommitSubject = new(@"\(#(?<number>\d+)\)$");

    public async Task<GateVerdict> DecideAsync(GateEvent evt, DateTimeOffset now, CancellationToken ct = default)
    {
        if (evt.Name != "merge_group")
            return GateVerdict.Pass("Gate passed", $"The gate enforces locks only on merge groups. This is a `{evt.Name}` event.");

        try
        {
            return await DecideMergeGroupAsync(evt, now, ct);
        }
        catch (Exception e) when (e is GitHubApiException or KeyNotFoundException or InvalidOperationException)
        {
            // KeyNotFoundException and InvalidOperationException: a response without the expected shape.
            return GateVerdict.FailOpen(GateVerdict.ApiError, LockStatusUnknown,
                $"The gate could not read the lock state or the group's pull requests from the GitHub API: {e.Message}. " +
                "The merge group passes. The watcher reports any unlabelled merge made during a lock (ADR-008).");
        }
    }

    async Task<GateVerdict> DecideMergeGroupAsync(GateEvent evt, DateTimeOffset now, CancellationToken ct)
    {
        var issues = await api.GetAllPagesAsync($"repos/{repository}/issues?state=open&labels={LockLabel}&per_page=100", ct);
        var locks = issues
            .Where(issue => !issue.TryGetProperty("pull_request", out _)
                && issue.GetProperty("user").GetProperty("login").GetString() == botLogin
                && issue.GetProperty("user").GetProperty("type").GetString() == "Bot")
            .Select(issue => new LockIssue(
                issue.GetProperty("number").GetInt32(),
                issue.GetProperty("html_url").GetString() ?? "",
                LockLease.ReadLeaseUntil(issue.GetProperty("body").GetString())))
            .OrderBy(issue => issue.Number)
            .ToList();

        if (locks.Count == 0)
            return GateVerdict.Pass("Gate passed", $"No open `{LockLabel}` issue authored by `{botLogin}`.");

        var enforced = locks.FirstOrDefault(issue => LockLease.IsValid(issue.LeaseUntil, now));
        if (enforced is null)
        {
            var leases = string.Join(", ", locks.Select(issue =>
                $"#{issue.Number} ({(issue.LeaseUntil is { } lease ? $"lease_until={lease:yyyy-MM-ddTHH:mm:ssZ}" : "no readable lease")})"));
            return GateVerdict.FailOpen(GateVerdict.LeaseExpired, LockLeaseExpired,
                $"The lock is open but its lease is missing, unreadable, expired or more than 24 h ahead: {leases}. " +
                "The watcher has stopped renewing it, so the merge group passes (ADR-014).");
        }

        var pullRequests = await FindGroupPullRequestsAsync(evt, ct);
        var lockText = $"`main` is locked by #{enforced.Number} ({enforced.Url})";
        if (pullRequests.Count == 0)
            return GateVerdict.Fail("Gate failed: main is locked",
                $"{lockText}, and the gate found no pull request in this merge group, so it cannot confirm that every PR is labelled `{FixLabel}`.");

        var unlabelled = pullRequests.Where(pr => !pr.Labels.Contains(FixLabel)).ToList();
        if (unlabelled.Count > 0)
            return GateVerdict.Fail("Gate failed: main is locked",
                $"{lockText}. Only PRs labelled `{FixLabel}` can merge. Not labelled: " +
                string.Join(", ", unlabelled.Select(pr => $"#{pr.Number} {pr.Title}")) + ".");

        return GateVerdict.Pass("Gate passed: every PR is a fix",
            $"{lockText}, but every PR in this merge group is labelled `{FixLabel}`: " +
            string.Join(", ", pullRequests.Select(pr => $"#{pr.Number}")) + ".");
    }

    /// <summary>
    /// Finds the PRs in a merge group from the commits between <c>base_sha</c> and <c>head_sha</c> (A-5):
    /// the open PRs associated with each commit, PRs named in merge or squash commit subjects, and the
    /// PR in the queue branch name. Only open PRs into the queue's branch count. Including a PR that is
    /// not in the group can only make the gate stricter.
    /// </summary>
    async Task<List<PullRequest>> FindGroupPullRequestsAsync(GateEvent evt, CancellationToken ct)
    {
        var queueBranch = evt.HeadRef is null ? null : QueueBranch.Match(evt.HeadRef);
        var targetBranch = queueBranch is { Success: true } ? queueBranch.Groups["branch"].Value : null;
        var found = new SortedDictionary<int, PullRequest>();
        var named = new SortedSet<int>();

        void Add(JsonElement pr)
        {
            if (pr.GetProperty("state").GetString() != "open")
                return;
            if (targetBranch is not null && pr.GetProperty("base").GetProperty("ref").GetString() != targetBranch)
                return;
            var number = pr.GetProperty("number").GetInt32();
            found[number] = new PullRequest(
                number,
                pr.GetProperty("title").GetString() ?? "",
                pr.GetProperty("labels").EnumerateArray().Select(label => label.GetProperty("name").GetString() ?? "").ToHashSet());
        }

        if (queueBranch is { Success: true })
            named.Add(int.Parse(queueBranch.Groups["number"].Value));

        if (evt.BaseSha is not null && evt.HeadSha is not null)
        {
            foreach (var commit in await CompareAsync(evt.BaseSha, evt.HeadSha, ct))
            {
                foreach (var pr in await api.GetAllPagesAsync($"repos/{repository}/commits/{commit.Sha}/pulls?per_page=100", ct))
                    Add(pr);

                var subject = commit.Message.Split('\n')[0].Trim();
                foreach (var pattern in new[] { MergeCommitSubject, SquashCommitSubject })
                    if (pattern.Match(subject) is { Success: true } match)
                        named.Add(int.Parse(match.Groups["number"].Value));
            }
        }

        foreach (var number in named.Where(number => !found.ContainsKey(number)))
            Add(await api.GetAsync($"repos/{repository}/pulls/{number}", ct));

        return [.. found.Values];
    }

    async Task<List<Commit>> CompareAsync(string baseSha, string headSha, CancellationToken ct)
    {
        const int pageSize = 100;
        var commits = new List<Commit>();
        for (var page = 1; ; page++)
        {
            var comparison = await api.GetAsync($"repos/{repository}/compare/{baseSha}...{headSha}?per_page={pageSize}&page={page}", ct);
            var pageCommits = comparison.GetProperty("commits").EnumerateArray()
                .Select(c => new Commit(c.GetProperty("sha").GetString() ?? "", c.GetProperty("commit").GetProperty("message").GetString() ?? ""))
                .ToList();
            commits.AddRange(pageCommits);
            if (pageCommits.Count < pageSize || commits.Count >= comparison.GetProperty("total_commits").GetInt32())
                return commits;
        }
    }

    sealed record LockIssue(int Number, string Url, DateTimeOffset? LeaseUntil);

    sealed record PullRequest(int Number, string Title, HashSet<string> Labels);

    sealed record Commit(string Sha, string Message);
}
