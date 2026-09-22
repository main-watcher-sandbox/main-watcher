using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MainWatcher.Core;

/// <summary>Shared GitHub model and REST boundary. The caller supplies an installation token.</summary>
public sealed class GitHubGateway(HttpClient http, long appId,
    Func<TimeSpan, CancellationToken, Task>? delay = null, Action<string>? log = null) : IGitHubGateway
{
    public const string CheckName = "main-watcher";
    public const string Workflow = "main-watcher-tests.yml";
    /// <summary>The watcher's own workflow in the watcher repo, dispatched per target and swept hourly (ADR-010).</summary>
    public const string WatchWorkflow = "watch.yml";
    /// <summary>The prefix of a per-target <c>watch.yml</c> <c>run-name</c>: how a cycle for one target is recognised.</summary>
    public const string WatchRunPrefix = "watch ";
    /// <summary>The gate workflow targets copy from <c>templates/main-watcher-gate.yml</c>.</summary>
    public const string GateWorkflow = "main-watcher-gate.yml";
    /// <summary>The gate template's job that runs only when the gate could not enforce a lock (ADR-008).</summary>
    public const string FailOpenJob = "main-watcher/gate-fail-open";
    /// <summary>At most this many gate runs are examined in one sweep, newest first, so a busy queue cannot lengthen it.</summary>
    public const int FailOpenRuns = 50;
    /// <summary>
    /// How much older than the window a gate run may be and still be examined. The fail-open job is <c>needs: gate</c>, so it
    /// exists only once the gate job has finished, up to its 10-minute timeout plus queue time after the run was created. The
    /// window itself is judged on the job's own time, so this lag only widens what is looked at, never what is reported.
    /// </summary>
    public static readonly TimeSpan FailOpenLag = TimeSpan.FromHours(1);
    readonly Dictionary<string, (string Head, List<CheckRun> Checks)> snapshots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The lowest rate-limit budget GitHub reported in the current window: what the token's installation has left of its
    /// hourly requests, and when it refills (R-13). Every target shares one installation's budget, and the scenario suite's
    /// fourth run spent it all (#25). The lowest, not the latest, because GitHub answered one sweep from two budgets at once
    /// (#60), and the one that runs out is the one that matters.
    /// </summary>
    public RateLimit? Budget { get; private set; }

    /// <summary>
    /// The first request GitHub refused on its rate limit, primary or secondary, as the error said it; null when none was.
    /// A cycle can fail on it anywhere, so the watcher reads it here rather than from whichever step failed (#60).
    /// </summary>
    public string? RateLimited { get; private set; }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> requests = new();

    /// <summary>
    /// How many requests this gateway has sent, by endpoint with its numbers and SHAs generalised, most first: what a cycle
    /// costs the installation's shared budget, and where (R-13, #60).
    /// </summary>
    public IReadOnlyList<(string Endpoint, int Count)> Requests =>
        requests.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).Select(r => (r.Key, r.Value)).ToArray();

    void Count(HttpMethod method, string path)
    {
        var resource = path.Split('?')[0].Replace("https://api.github.com/", "").TrimStart('/');
        resource = System.Text.RegularExpressions.Regex.Replace(resource, "(?<=/)([0-9a-f]{40}|[0-9]+)(?=/|$)", "{n}");
        requests.AddOrUpdate($"{method} {resource}", 1, (_, n) => n + 1);
    }

    /// <summary>
    /// An installation token for the App this gateway authenticates as (a JWT client), limited to <paramref name="repo"/>.
    /// </summary>
    public async Task<InstallationToken> InstallationToken(string repo, CancellationToken ct)
    {
        var installation = (await Send(HttpMethod.Get, $"repos/{repo}/installation", null, ct)).GetProperty("id").GetInt64();
        var json = await Send(HttpMethod.Post, $"app/installations/{installation}/access_tokens",
            new { repositories = new[] { repo.Split('/')[1] } }, ct);
        return new(json.GetProperty("token").GetString()!, Date(json, "expires_at")
            ?? throw new InvalidDataException("GitHub returned an installation token without expires_at."));
    }

    public async Task ValidateTarget(Target target, CancellationToken ct)
    {
        var file = await Send(HttpMethod.Get, $"repos/{target.Repo}/contents/.github/workflows/{Workflow}?ref=main", null, ct);
        var yaml = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(file.GetProperty("content").GetString()!));
        CallerConfiguration.Validate(target, yaml);
    }

    async Task<JsonElement> Send(HttpMethod method, string path, object? body, CancellationToken ct, Action<string?>? nextPage = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, path);
                if (body is not null) request.Content = JsonContent.Create(body);
                Count(method, path);
                using var response = await http.SendAsync(request, ct);
                Note(response);
                if (method == HttpMethod.Get && attempt < 3 && IsTransient(response))
                {
                    await (delay ?? Task.Delay)(TimeSpan.FromSeconds(2 * Math.Pow(2, attempt)), ct);
                    continue;
                }
                await Ensure(response, method, path, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                nextPage?.Invoke(NextPage(response));
                if (string.IsNullOrWhiteSpace(text)) return default;
                using var document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
            catch (HttpRequestException e) when (method == HttpMethod.Get && attempt < 3 && e.StatusCode is null) { }
            catch (TaskCanceledException) when (method == HttpMethod.Get && attempt < 3 && !ct.IsCancellationRequested) { }
            await (delay ?? Task.Delay)(TimeSpan.FromSeconds(2 * Math.Pow(2, attempt)), ct);
        }
    }

    /// <summary>
    /// Throws for a refused request, naming it and saying what GitHub said, with its rate-limit headers. A bare "403
    /// (Forbidden)" could not tell a missing permission from an exhausted or secondary rate limit: the scenario suite saw
    /// every cycle fail with only that (#25).
    /// </summary>
    async Task Ensure(HttpResponseMessage response, HttpMethod method, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            message = json.RootElement.ValueKind == JsonValueKind.Object ? Text(json.RootElement, "message") : null;
        }
        catch (JsonException) { }
        var limits = string.Join(", ", new[] { "x-ratelimit-resource", "x-ratelimit-remaining", "x-ratelimit-reset", "retry-after" }
            .Select(name => response.Headers.TryGetValues(name, out var values) ? $"{name}: {values.First()}" : null).OfType<string>());
        // The path without its query: the resource is what matters, and a page link's query is long.
        var resource = "/" + path.Split('?')[0].Replace("https://api.github.com/", "").TrimStart('/');
        var error = $"GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}) to {method} {resource}"
            + (message is null ? "" : $": {message}") + (limits.Length == 0 ? "" : $" [{limits}]");
        if (RateLimit.Refused(response)) RateLimited ??= error;
        throw new HttpRequestException(error, null, response.StatusCode);
    }

    void Note(HttpResponseMessage response) => Budget = RateLimit.Lower(Budget, RateLimit.Read(response));

    static bool IsTransient(HttpResponseMessage response) => (int)response.StatusCode >= 500 || RateLimit.Refused(response);

    static string? NextPage(HttpResponseMessage response) => response.Headers.TryGetValues("Link", out var links)
        ? links.Select(link => System.Text.RegularExpressions.Regex.Match(link, "<([^>]+)>;\\s*rel=\"next\""))
            .FirstOrDefault(match => match.Success)?.Groups[1].Value : null;

    async Task<List<JsonElement>> Pages(string path, string? key, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        await foreach (var item in Items(path, key, ct)) all.Add(item);
        return all;
    }

    async IAsyncEnumerable<JsonElement> Items(string path, string? key,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? next = $"{path}{(path.Contains('?') ? '&' : '?')}per_page=100&page=1";
        while (next is not null)
        {
            var uri = new Uri(http.BaseAddress!, next);
            if (uri.GetLeftPart(UriPartial.Authority) != http.BaseAddress!.GetLeftPart(UriPartial.Authority))
                throw new InvalidDataException("GitHub pagination link points outside the API origin.");
            var json = await Send(HttpMethod.Get, uri.AbsoluteUri, null, ct, value => next = value);
            var items = (key is null ? json : json.GetProperty(key)).EnumerateArray().ToArray();
            foreach (var item in items) yield return item;
        }
    }

    static string? Text(JsonElement json, string key) => json.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static DateTimeOffset? Date(JsonElement json, string key) => DateTimeOffset.TryParse(Text(json, key), out var date) ? date : null;
    static CheckRun Check(JsonElement json) => new(json.GetProperty("id").GetInt64(), Text(json, "head_sha")!,
        Text(json, "status")!, Text(json, "conclusion"), Date(json, "started_at") ?? DateTimeOffset.MinValue,
        Date(json, "completed_at"), Text(json, "external_id"),
        json.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object ? Text(output, "title") : null,
        json.TryGetProperty("output", out var body) && body.ValueKind == JsonValueKind.Object ? Text(body, "summary") : null);

    public async Task<string> MainHead(string repo, CancellationToken ct) =>
        (await Send(HttpMethod.Get, $"repos/{repo}/commits/main", null, ct)).GetProperty("sha").GetString()!;

    /// <summary>
    /// How many commits back a first read of a target's check runs looks for its last green run. Each commit costs a request,
    /// and reading them all cost every <c>watch.yml</c> cycle about 195 of its 210 requests on a sandbox target with 195
    /// commits (#60): a cost that grew with every push, and on a target with a long history more than the hour's budget.
    /// </summary>
    public const int ChecksLimit = 50;

    /// <summary>
    /// This App's check runs on <c>main</c>'s history, as far back as the rules that read them need: the newest ones, any
    /// pending, and the last green run. A first read walks back from the head only until it reaches a commit with a green run,
    /// or for <see cref="ChecksLimit"/> commits. Only the Planner creates check runs, on the head, and never while one is
    /// pending (ADR-017), so every check run on an older commit is older than those on a newer one, and a pending one is always
    /// the newest. What the limit leaves out is a last green run further back, which only the timing comparison reads (the push
    /// list walks back on its own), and a pending run with more than that many pushes after it.
    /// </summary>
    public async Task<IReadOnlyList<CheckRun>> Checks(string repo, CancellationToken ct)
    {
        // Bootstrap once per gateway lifetime; subsequent worker polls refresh only
        // pending checks, the head, and commits added since the previous snapshot.
        var head = await MainHead(repo, ct);
        var hasSnapshot = snapshots.TryGetValue(repo, out var snapshot);
        var result = hasSnapshot ? snapshot.Checks.ToList() : [];
        var refresh = result.Where(c => c.Status != "completed").Select(c => c.Sha).ToHashSet(StringComparer.Ordinal);
        if (hasSnapshot) refresh.Add(snapshot.Head);
        refresh.Add(head);
        var read = new Dictionary<string, IReadOnlyList<CheckRun>>(StringComparer.Ordinal);
        if (!hasSnapshot || snapshot.Head != head)
        {
            var walked = 0;
            await foreach (var commit in Items($"repos/{repo}/commits?sha={head}", null, ct))
            {
                var sha = commit.GetProperty("sha").GetString()!;
                if (hasSnapshot)
                {
                    if (sha == snapshot.Head) break;
                    refresh.Add(sha);
                    continue;
                }
                var checks = read[sha] = await CommitChecks(repo, sha, ct);
                if (checks.Any(c => c is { Status: "completed", Conclusion: "success" }) || ++walked >= ChecksLimit) break;
            }
        }
        foreach (var sha in refresh.Concat(read.Keys).Distinct(StringComparer.Ordinal))
        {
            var checks = read.TryGetValue(sha, out var known) ? known : await CommitChecks(repo, sha, ct);
            result.RemoveAll(c => c.Sha == sha);
            result.AddRange(checks);
        }
        // Publish only a fully refreshed snapshot; failed reads never make partial state authoritative.
        snapshots[repo] = (head, result);
        return result;
    }

    public async Task<IReadOnlyList<CheckRun>> CommitChecks(string repo, string sha, CancellationToken ct) =>
        (await Pages($"repos/{repo}/commits/{sha}/check-runs?check_name={CheckName}&filter=all", "check_runs", ct))
        .Where(c => c.GetProperty("app").GetProperty("id").GetInt64() == appId).Select(Check).ToArray();

    public async Task<IReadOnlyList<string>> History(string repo, string sha, int limit, CancellationToken ct)
    {
        var shas = new List<string>();
        await foreach (var commit in Items($"repos/{repo}/commits?sha={sha}", null, ct))
        {
            shas.Add(commit.GetProperty("sha").GetString()!);
            // Stop before reading a page that would not be used.
            if (shas.Count == limit) break;
        }
        return shas;
    }

    public async Task<IReadOnlyList<Push>> Pushes(string repo, int limit, CancellationToken ct)
    {
        var pushes = new List<Push>();
        // Activity pages by cursor; the next link carries it.
        await foreach (var entry in Items($"repos/{repo}/activity?ref=main&direction=desc", null, ct))
        {
            pushes.Add(new(Text(entry, "before") ?? "", Text(entry, "after") ?? "", Date(entry, "timestamp") ?? DateTimeOffset.MinValue,
                Text(entry, "activity_type") ?? "", entry.TryGetProperty("actor", out var actor) && actor.ValueKind == JsonValueKind.Object ? Text(actor, "login") : null));
            if (pushes.Count == limit) break;
        }
        return pushes;
    }

    public async Task<int?> CommitCount(string repo, string before, string after, CancellationToken ct)
    {
        if (!IsSha(before) || !IsSha(after) || before.All(c => c == '0') || after.All(c => c == '0')) return null;
        try
        {
            // One commit per page keeps the response small; total_commits still counts them all.
            var json = await Send(HttpMethod.Get, $"repos/{repo}/compare/{before}...{after}?per_page=1", null, ct);
            return json.TryGetProperty("total_commits", out var total) ? total.GetInt32() : null;
        }
        // A force push can leave "before" unreachable, and GitHub cannot compare it any more.
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) { return null; }
    }

    static bool IsSha(string text) => text.Length == 40 && text.All(char.IsAsciiHexDigit);

    public async Task<CheckRun> CreateCheck(string repo, string sha, DateTimeOffset now, string title, string summary, CancellationToken ct) =>
        Check(await Send(HttpMethod.Post, $"repos/{repo}/check-runs",
            new { name = CheckName, head_sha = sha, status = "in_progress", started_at = now, output = new { title, summary } }, ct));

    public async Task<long?> Dispatch(string repo, string sha, long checkId, CancellationToken ct)
    {
        try
        {
            var json = await Send(HttpMethod.Post, $"repos/{repo}/actions/workflows/{Workflow}/dispatches",
                new { @ref = "main", return_run_details = true, inputs = new { sha, check_run_id = checkId.ToString(System.Globalization.CultureInfo.InvariantCulture) } }, ct);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("workflow_run_id", out var id)) return id.GetInt64();
            return NoRun(repo, checkId, "the response carried no workflow_run_id");
        }
        // The reason is only logged: the POST is never retried, and recovery links or releases the check.
        catch (HttpRequestException e) when (e.StatusCode is null || (int)e.StatusCode >= 500)
        {
            return NoRun(repo, checkId, e.StatusCode is { } status ? $"HTTP {(int)status} ({e.Message})" : $"network error ({e.Message})");
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { return NoRun(repo, checkId, $"timed out ({e.Message})"); }
    }

    public async Task DispatchWorkflow(string repo, string workflow, IReadOnlyDictionary<string, string> inputs, CancellationToken ct) =>
        // Never retried: a lost response may still have started the workflow.
        await Send(HttpMethod.Post, $"repos/{repo}/actions/workflows/{workflow}/dispatches", new { @ref = "main", inputs }, ct);

    long? NoRun(string repo, long checkId, string reason)
    {
        log?.Invoke($"Check {checkId}: dispatch of {Workflow} in {repo} returned no run: {reason}.");
        return null;
    }

    public async Task<IReadOnlyList<WorkflowRun>> Runs(string repo, string workflow, DateTimeOffset since, CancellationToken ct) =>
        (await Pages($"repos/{repo}/actions/workflows/{workflow}/runs?event=workflow_dispatch&created={Uri.EscapeDataString(">=" + since.ToString("O"))}", "workflow_runs", ct))
        .Select(r => new WorkflowRun(r.GetProperty("id").GetInt64(), Text(r, "display_title")!, Date(r, "created_at")!.Value, Text(r, "status")!, Date(r, "updated_at"))).ToArray();

    public async Task<IReadOnlyList<FailOpen>> FailOpens(string repo, DateTimeOffset since, CancellationToken ct)
    {
        List<JsonElement> runs;
        try
        {
            // Runs from before the window are read too: one created just before it can post its fail-open check run inside it.
            var created = Uri.EscapeDataString(">=" + (since - FailOpenLag).ToString("O"));
            runs = await Pages($"repos/{repo}/actions/workflows/{GateWorkflow}/runs?event=merge_group&created={created}", "workflow_runs", ct);
        }
        // A target that has not copied the gate workflow, or has renamed it, has no such runs to report.
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return []; }
        var found = new List<FailOpen>();
        foreach (var run in runs.OrderByDescending(r => r.GetProperty("id").GetInt64()).Take(FailOpenRuns))
        {
            var id = run.GetProperty("id").GetInt64();
            // The template skips this job unless the gate failed open, so a job with any other conclusion, or none yet, is one.
            var jobs = await Pages($"repos/{repo}/actions/runs/{id}/jobs?filter=latest", "jobs", ct);
            if (jobs.Where(j => Text(j, "name") == FailOpenJob && Text(j, "conclusion") != "skipped")
                .Select(j => (JsonElement?)j).FirstOrDefault() is not { } job) continue;
            // ADR-008 counts the check run when it was posted, not when its run started, so each sweep's window abuts the last
            // one's: a job that appeared after the previous sweep read it belongs to this one, and is reported exactly once.
            var at = Date(job, "started_at") ?? Date(run, "created_at");
            if (at < since) continue;
            found.Add(new(id, Text(run, "head_sha") ?? "", Text(run, "head_branch") ?? "", at ?? since));
        }
        return found;
    }

    public async Task<IReadOnlyList<GateBlock>> GateBlocks(string repo, DateTimeOffset since, CancellationToken ct)
    {
        List<JsonElement> runs;
        try
        {
            // Runs created before the window are read too, because the queue sweep re-runs gate runs from before the lock: such
            // a run fails inside the window on an attempt started inside it, while GitHub still dates it by its first attempt
            // (ADR-016). The window itself is judged on the latest attempt's own time, so the lag only widens what is looked
            // at, never what is reported.
            var created = Uri.EscapeDataString(">=" + (since - QueueLag).ToString("O"));
            runs = await Pages($"repos/{repo}/actions/workflows/{GateWorkflow}/runs?event=merge_group&status=failure&created={created}", "workflow_runs", ct);
        }
        // A target that has not copied the gate workflow, or has renamed it, has no such runs to report.
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return []; }
        var blocked = new List<GateBlock>();
        foreach (var run in runs)
        {
            // The queue branch names the entry's own pull request, which is the one the queue removes when the gate fails it.
            if (QueueBranch.Match(Text(run, "head_branch") ?? "") is not { Success: true } match) continue;
            var at = Date(run, "run_started_at") ?? Date(run, "created_at") ?? since;
            if (at < since) continue;
            blocked.Add(new(run.GetProperty("id").GetInt64(), int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture),
                Text(run, "head_branch")!, at, Attempt(run)));
        }
        return blocked.OrderBy(b => b.At).ToArray();
    }

    /// <summary>
    /// How long before a lock opened a merge group's gate run may have been created and still be reported as removed by this
    /// lock. It bounds what <see cref="GateBlocks"/> reads: a group waits in the queue for as long as its other required checks
    /// take, and a day is well past any of them (ADR-016).
    /// </summary>
    public static readonly TimeSpan QueueLag = TimeSpan.FromHours(24);

    static int Attempt(JsonElement run) =>
        run.TryGetProperty("run_attempt", out var attempt) && attempt.ValueKind == JsonValueKind.Number ? attempt.GetInt32() : 1;

    public async Task<IReadOnlyList<QueuedGroup>> QueuedGroups(string repo, CancellationToken ct)
    {
        JsonElement refs;
        // Matching refs, not the branch list: the queue branches are a prefix, and a repository's other branches are none of
        // this sweep's business.
        try { refs = await Send(HttpMethod.Get, $"repos/{repo}/git/matching-refs/heads/{QueuePrefix}", null, ct); }
        // An empty queue answers with an empty array, but a repository that has never had one can answer 404.
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return []; }
        if (refs.ValueKind != JsonValueKind.Array) return [];
        var groups = new List<QueuedGroup>();
        foreach (var item in refs.EnumerateArray())
        {
            var branch = (Text(item, "ref") ?? "").StartsWith("refs/heads/", StringComparison.Ordinal)
                ? Text(item, "ref")!["refs/heads/".Length..] : Text(item, "ref") ?? "";
            if (!item.TryGetProperty("object", out var head) || Text(head, "sha") is not { } sha) continue;
            var match = QueueBranch.Match(branch);
            groups.Add(new(branch, sha, match.Success
                ? int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture) : null));
        }
        return groups;
    }

    /// <summary>The ref prefix of the merge queue's temporary branches for <c>main</c> (A-7, R-22).</summary>
    public const string QueuePrefix = "gh-readonly-queue/main/";

    public async Task<IReadOnlyList<GateRun>> GateRuns(string repo, string sha, CancellationToken ct)
    {
        try
        {
            return (await Pages($"repos/{repo}/actions/workflows/{GateWorkflow}/runs?event=merge_group&head_sha={sha}", "workflow_runs", ct))
                .Select(r => new GateRun(r.GetProperty("id").GetInt64(), Text(r, "status") ?? "", Text(r, "conclusion"),
                    // The latest attempt's start, so a run this sweep has already re-run is not re-run again.
                    Date(r, "run_started_at") ?? Date(r, "created_at") ?? DateTimeOffset.MinValue))
                .OrderByDescending(r => r.Id).ToArray();
        }
        // A target that has not copied the gate workflow, or has renamed it, has no gate to re-run.
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return []; }
    }

    public async Task<string?> Rerun(string repo, long runId, CancellationToken ct)
    {
        try
        {
            await Send(HttpMethod.Post, $"repos/{repo}/actions/runs/{runId}/rerun", null, ct);
            return null;
        }
        // 403 is GitHub refusing to re-run a run that is already running, which the next cycle sees as the new attempt it is.
        // Nothing here is fatal: the sweep stays owed, so a refusal that lasts becomes the "queue sweep unfinished" alert.
        catch (HttpRequestException e) { return e.StatusCode is { } status ? $"HTTP {(int)status}" : e.Message; }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { return $"timed out ({e.Message})"; }
    }

    /// <summary>
    /// A merge-queue branch, <c>gh-readonly-queue/&lt;branch&gt;/pr-&lt;number&gt;-&lt;base sha&gt;</c> (A-7, confirmed in the
    /// sandbox on 2026-09-16). The gate reads the same shape through its own copy, because it shares no code with the watcher.
    /// </summary>
    static readonly System.Text.RegularExpressions.Regex QueueBranch =
        new(@"^gh-readonly-queue/.+/pr-(?<number>\d+)-[0-9a-f]{40}$");

    /// <remarks>
    /// The pull request is read from the commit subject, not from the commits-to-pull-requests API, because that endpoint
    /// needs a Pull requests permission the <c>main-watcher</c> App does not hold (§8, ADR-008). The subjects are the ones the
    /// gate already matches, and a merge group's own merge commit always carries one.
    /// </remarks>
    public async Task<MergedRange?> MergedCommits(string repo, string before, string after, int skip, CancellationToken ct)
    {
        if (!IsSha(before) || !IsSha(after) || before.All(c => c == '0') || after.All(c => c == '0')) return null;
        var commits = new List<MergedCommit>();
        var total = 0;
        try
        {
            // A merge group holds one queue entry's own pull request and everything ahead of it, so the whole range is read,
            // not only the head commit. It is read to the end, because a pull request is named by its **last** commit, the
            // merge or squash commit: stopping early would drop exactly the commits that name the later pull requests, and the
            // earlier ones it did name would hide that anything was missing. A range too long for one pass is continued from
            // where the last one stopped, so the entry is finished across several rather than left part-judged.
            // `total_commits` counts the whole range however much of it one response carries, so it is what says whether the
            // read reached the end.
            var first = true;
            for (var page = skip / ComparePageSize + 1; ; page++)
            {
                var comparison = await Send(HttpMethod.Get,
                    $"repos/{repo}/compare/{before}...{after}?per_page={ComparePageSize}&page={page}", null, ct);
                total = comparison.TryGetProperty("total_commits", out var counted) && counted.ValueKind == JsonValueKind.Number
                    ? counted.GetInt32() : 0;
                var read = comparison.GetProperty("commits").EnumerateArray().Select(Merged).ToArray();
                // The first page read may start part-way into itself, where an earlier pass stopped inside it.
                commits.AddRange(first ? read.Skip(skip % ComparePageSize) : read);
                first = false;
                if (read.Length < ComparePageSize || skip + commits.Count >= total || commits.Count >= MaxMergedCommits) break;
            }
        }
        // A force push can leave "before" unreachable, and GitHub cannot compare it any more.
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) { return null; }
        // A page can carry the bound past itself, so the pass keeps exactly what it promised to read.
        if (commits.Count > MaxMergedCommits) commits.RemoveRange(MaxMergedCommits, commits.Count - MaxMergedCommits);
        return new(commits, skip + commits.Count < total);
    }

    static MergedCommit Merged(JsonElement json)
    {
        var commit = json.GetProperty("commit");
        var subject = (Text(commit, "message") ?? "").Split('\n')[0].Trim();
        return new(Text(json, "sha") ?? "", subject,
            Date(commit.GetProperty("committer"), "date") ?? DateTimeOffset.MinValue, Reconciliation.PullOf(subject));
    }

    /// <summary>The compare API's largest page.</summary>
    const int ComparePageSize = 100;

    /// <summary>
    /// How many commits of one activity entry one pass reads. It is a budget, not a limit: a longer range is continued by the
    /// next pass from where this one stopped, so every pull request it merged is still named and judged, while no single cycle
    /// spends a hundred requests on one enormous merge (R-13).
    /// </summary>
    public const int MaxMergedCommits = 500;

    public async Task<IReadOnlyList<PullEvent>> PullEvents(string repo, int number, CancellationToken ct) =>
        // Oldest first, as GitHub returns them: two events in the same second are told apart by their order, not their times.
        // A pull request's merge is in this same timeline, so the moment its labels are judged at costs no extra request and
        // no Pull requests permission.
        (await Pages($"repos/{repo}/issues/{number}/events", null, ct))
        .Where(e => Text(e, "event") is "labeled" or "unlabeled" or Reconciliation.Merged)
        .Select(e => new PullEvent(Text(e, "event")!,
            e.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.Object ? Text(label, "name") : null,
            Date(e, "created_at") ?? DateTimeOffset.MinValue))
        .Where(e => e.Name == Reconciliation.Merged || e.Label is not null).ToArray();

    public async Task Link(string repo, long checkId, long runId, CancellationToken ct) =>
        await Send(HttpMethod.Patch, $"repos/{repo}/check-runs/{checkId}", new { external_id = runId.ToString(System.Globalization.CultureInfo.InvariantCulture), details_url = $"https://github.com/{repo}/actions/runs/{runId}" }, ct);

    public async Task<IReadOnlyList<WorkflowJob>?> Jobs(string repo, long runId, CancellationToken ct)
    {
        try
        {
            var jobs = await Pages($"repos/{repo}/actions/runs/{runId}/jobs?filter=latest", "jobs", ct);
            var parsed = jobs.Select(j => new WorkflowJob(Text(j, "name")!, Text(j, "status")!,
                j.TryGetProperty("steps", out var steps) ? steps.EnumerateArray().Select(s => new JobStep(Text(s, "name")!, Text(s, "conclusion"))).ToArray() : [],
                Date(j, "completed_at"), Date(j, "started_at"))).ToArray();
            if (!parsed.Any(j => Outcomes.IsTestJob(j.Name)))
            {
                var run = await Send(HttpMethod.Get, $"repos/{repo}/actions/runs/{runId}", null, ct);
                if (Text(run, "status") == "completed") return [];
                // No job has been materialized yet. Preserve the pending state.
                return [new("main-watcher", "queued", [])];
            }
            return parsed;
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<CtrfResult> Reports(string repo, long runId, CancellationToken ct)
    {
        try
        {
            var artifacts = (await Pages($"repos/{repo}/actions/runs/{runId}/artifacts", "artifacts", ct))
                .Where(a => Text(a, "name") == "main-watcher-ctrf" && !a.GetProperty("expired").GetBoolean()).ToArray();
            if (artifacts.Length != 1) return CtrfResult.Unknown;
            var id = artifacts[0].GetProperty("id").GetInt64();
            Count(HttpMethod.Get, $"repos/{repo}/actions/artifacts/{id}/zip");
            using var response = await http.GetAsync($"repos/{repo}/actions/artifacts/{id}/zip", HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > CtrfReader.MaxReportBytes) return CtrfResult.Unknown;
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > CtrfReader.MaxReportBytes) return CtrfResult.Unknown;
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            return CtrfReader.ReadZip(buffer);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException) { return CtrfResult.Unknown; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return CtrfResult.Unknown; }
    }

    public async Task<string?> CancelRun(string repo, long runId, bool force, CancellationToken ct)
    {
        try
        {
            await Send(HttpMethod.Post, $"repos/{repo}/actions/runs/{runId}/{(force ? "force-cancel" : "cancel")}", null, ct);
            return null;
        }
        // 409 means the run has already finished or cannot be cancelled, and 403 that a force-cancel came too early. Nothing
        // here is fatal: the check run stays in_progress, the next cycle asks again, and the 15-minute steps escalate.
        catch (HttpRequestException e) { return e.StatusCode is { } status ? $"HTTP {(int)status}" : e.Message; }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { return $"timed out ({e.Message})"; }
    }

    public async Task Output(string repo, long checkId, string title, string summary, CancellationToken ct) =>
        // No status: the check run stays in_progress while the run is being stopped (ADR-013 point 5).
        await Send(HttpMethod.Patch, $"repos/{repo}/check-runs/{checkId}", new { output = new { title, summary } }, ct);

    public async Task Complete(string repo, long checkId, string conclusion, string title, string summary, CancellationToken ct) =>
        await Send(HttpMethod.Patch, $"repos/{repo}/check-runs/{checkId}", new
        {
            status = "completed", conclusion, completed_at = DateTimeOffset.UtcNow,
            output = new { title, summary }
        }, ct);

    public async Task<string?> File(string repo, string path, CancellationToken ct)
    {
        try
        {
            var file = await Send(HttpMethod.Get, $"repos/{repo}/contents/{path}?ref=main", null, ct);
            // A directory is an array, not a file. An existing empty file is "", not null, so a
            // CODEOWNERS lookup stops at the first file that exists. Files over 1 MB come without
            // inline content and also read as "".
            if (file.ValueKind != JsonValueKind.Object || Text(file, "type") != "file") return null;
            return Text(file, "encoding") == "base64" && Text(file, "content") is { } content
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content)) : "";
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    static Issue ToIssue(JsonElement json) => new(json.GetProperty("number").GetInt32(), Text(json, "title") ?? "", Text(json, "body"),
        Text(json.GetProperty("user"), "login") ?? "", Text(json.GetProperty("user"), "type") ?? "", Text(json, "html_url") ?? "",
        Text(json, "state") ?? "open", Text(json, "state_reason"), Date(json, "updated_at"),
        json.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
        Date(json, "created_at"), Date(json, "closed_at"));

    static string Since(DateTimeOffset since) => Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));

    public async Task<IReadOnlyList<Issue>> OpenIssues(string repo, string label, CancellationToken ct) =>
        (await Pages($"repos/{repo}/issues?state=open&labels={Uri.EscapeDataString(label)}", null, ct))
        .Where(i => !i.TryGetProperty("pull_request", out _)).Select(ToIssue).ToArray();

    public async Task<IReadOnlyList<Issue>> Issues(string repo, string label, DateTimeOffset since, CancellationToken ct) =>
        (await Pages($"repos/{repo}/issues?state=all&labels={Uri.EscapeDataString(label)}&since={Since(since)}", null, ct))
        .Where(i => !i.TryGetProperty("pull_request", out _)).Select(ToIssue).ToArray();

    public async Task<IReadOnlyList<IssueComment>> Comments(string repo, int number, DateTimeOffset? since, CancellationToken ct) =>
        (await Pages($"repos/{repo}/issues/{number}/comments" + (since is { } time ? $"?since={Since(time)}" : ""), null, ct))
        .Select(c => c.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
            ? new IssueComment(Text(c, "body") ?? "", Text(user, "login") ?? "", Text(user, "type") ?? "")
            : new IssueComment(Text(c, "body") ?? "", "", "")).ToArray();

    public async Task<Account?> ClosedBy(string repo, int number, CancellationToken ct)
    {
        // The issue list omits closed_by; only a single issue carries it.
        var issue = await Send(HttpMethod.Get, $"repos/{repo}/issues/{number}", null, ct);
        return issue.TryGetProperty("closed_by", out var user) && user.ValueKind == JsonValueKind.Object && Text(user, "login") is { } login
            ? new(login, Text(user, "type") ?? "") : null;
    }

    public async Task EditBody(string repo, int number, string body, CancellationToken ct) =>
        await Send(HttpMethod.Patch, $"repos/{repo}/issues/{number}", new { body }, ct);

    public async Task<Issue> CreateIssue(string repo, string title, string body, string label, CancellationToken ct)
    {
        try { await Send(HttpMethod.Post, $"repos/{repo}/labels", new { name = label, color = "b60205" }, ct); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.UnprocessableEntity) { }
        return ToIssue(await Send(HttpMethod.Post, $"repos/{repo}/issues", new { title, body, labels = new[] { label } }, ct));
    }

    public async Task Comment(string repo, int number, string body, CancellationToken ct) =>
        await Send(HttpMethod.Post, $"repos/{repo}/issues/{number}/comments", new { body }, ct);

    public async Task Close(string repo, int number, string reason, long? duplicateOf, CancellationToken ct) =>
        // duplicate_issue_id takes the database ID: in the sandbox, an issue number linked an unrelated issue.
        await Send(HttpMethod.Patch, $"repos/{repo}/issues/{number}", duplicateOf is { } canonical
            ? new { state = "closed", state_reason = reason, duplicate_issue_id = canonical }
            : new { state = "closed", state_reason = reason }, ct);
}
