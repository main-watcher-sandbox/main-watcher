using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MainWatcher.Scenarios.Infra;

namespace MainWatcher.Scenarios.Sandbox;

/// <summary>
/// One sandbox target repo: its <c>main</c>, its switches, its check runs, locks, pull requests, merge queue and runs. The suite
/// commits through the Git data API as an organisation admin, which the merge-queue ruleset lets straight onto <c>main</c>.
/// </summary>
public sealed class Target(GitHub github, string repo, Templates templates)
{
    public const string SwitchesFile = "sandbox.json";
    public const string CallerFile = ".github/workflows/main-watcher-tests.yml";
    public const string GateFile = ".github/workflows/main-watcher-gate.yml";
    public const string CodeOwnersFile = ".github/CODEOWNERS";

    /// <summary>Where pull requests the suite opens put their one file, so resets can clear them.</summary>
    public const string ProbeFolder = "probes";

    public string Repo { get; } = repo;
    public string Name => Repo[(Repo.IndexOf('/') + 1)..];
    public GitHub GitHub => github;

    public override string ToString() => Repo;

    // ---- main and its files ----

    public async Task<string> Head(CancellationToken ct) =>
        (await github.Get($"repos/{Repo}/git/ref/heads/main", ct))!["object"]!["sha"]!.GetValue<string>();

    /// <summary>A file on <paramref name="reference"/>, or null if it does not exist.</summary>
    public async Task<string?> File(string path, CancellationToken ct, string reference = "main")
    {
        var node = await github.Find($"repos/{Repo}/contents/{path}?ref={reference}", ct);
        return node?["content"]?.GetValue<string>() is { } content
            ? Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", ""))) : null;
    }

    /// <summary>
    /// One commit on <paramref name="branch"/> that writes each file given text and deletes each file given null. The branch
    /// is created from <c>main</c> if it does not exist. Retried if <c>main</c> moved meanwhile, for example by a merge.
    /// </summary>
    public async Task<string> Commit(IReadOnlyDictionary<string, string?> files, string message, CancellationToken ct,
        string branch = "main")
    {
        for (var attempt = 1; ; attempt++)
        {
            var existing = await github.Find($"repos/{Repo}/git/ref/heads/{branch}", ct);
            var parent = existing?["object"]!["sha"]!.GetValue<string>() ?? await Head(ct);
            var tree = (await github.Get($"repos/{Repo}/git/commits/{parent}", ct))!["tree"]!["sha"]!.GetValue<string>();
            var entries = new JsonArray();
            foreach (var (path, content) in files)
            {
                if (content is null)
                {
                    // Deleting a file that is not there fails the whole tree, so only existing ones are deleted.
                    if (await github.Find($"repos/{Repo}/contents/{path}?ref={parent}", ct) is null) continue;
                    entries.Add(new JsonObject { ["path"] = path, ["mode"] = "100644", ["type"] = "blob", ["sha"] = null });
                }
                else entries.Add(new JsonObject { ["path"] = path, ["mode"] = "100644", ["type"] = "blob", ["content"] = content });
            }
            if (entries.Count == 0) return parent;
            var newTree = (await github.Post($"repos/{Repo}/git/trees", new JsonObject { ["base_tree"] = tree, ["tree"] = entries }, ct))!
                ["sha"]!.GetValue<string>();
            var commit = (await github.Post($"repos/{Repo}/git/commits",
                new JsonObject { ["message"] = message, ["tree"] = newTree, ["parents"] = new JsonArray(parent) }, ct))!["sha"]!.GetValue<string>();
            try
            {
                if (existing is null && branch != "main")
                    await github.Post($"repos/{Repo}/git/refs", new { @ref = $"refs/heads/{branch}", sha = commit }, ct);
                else
                    await github.Patch($"repos/{Repo}/git/refs/heads/{branch}", new { sha = commit, force = false }, ct);
                return commit;
            }
            catch (GitHubException e) when (attempt < 5 && (int)e.Status == 422)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    /// <summary>The switches on <c>main</c>.</summary>
    public async Task<JsonObject> Switches(CancellationToken ct) =>
        JsonNode.Parse(await File(SwitchesFile, ct) ?? throw new ScenarioFailure($"{Repo} has no {SwitchesFile}"))!.AsObject();

    /// <summary>Commits <c>sandbox.json</c> with <paramref name="edit"/> applied, and returns the new head.</summary>
    public async Task<string> SetSwitches(Action<JsonObject> edit, string message, CancellationToken ct,
        IReadOnlyDictionary<string, string?>? alsoFiles = null)
    {
        var switches = await Switches(ct);
        edit(switches);
        var files = new Dictionary<string, string?>(alsoFiles ?? new Dictionary<string, string?>())
        {
            [SwitchesFile] = Templates.Format(switches)
        };
        return await Commit(files, message, ct);
    }

    /// <summary>The caller workflow, pointed at a variant branch and with the inputs a scenario needs.</summary>
    public string Caller(string gateRef = "main", int timeoutMinutes = 30, string? runsOn = null) =>
        templates.Caller(gateRef, timeoutMinutes, runsOn);

    // ---- check runs ----

    /// <summary>Every <c>main-watcher</c> check run on a commit, oldest first, including earlier attempts.</summary>
    public async Task<List<CheckRun>> Checks(string sha, CancellationToken ct) =>
        (await github.All($"repos/{Repo}/commits/{sha}/check-runs?check_name=main-watcher&filter=all", ct, "check_runs"))
            .Where(c => c["app"]?["slug"]?.GetValue<string>() == SandboxOrg.MainWatcherApp)
            .Select(CheckRun.From).OrderBy(c => c.StartedAt).ThenBy(c => c.Id).ToList();

    public async Task<CheckRun> Check(long id, CancellationToken ct) => CheckRun.From((await github.Get($"repos/{Repo}/check-runs/{id}", ct))!);

    /// <summary>Waits for the <paramref name="index"/>th check run (from 1) on a commit to exist.</summary>
    public Task<CheckRun> AwaitCheck(string sha, TimeSpan timeout, CancellationToken ct, int index = 1) =>
        Poll.Until($"check run {index} on {Short(sha)} in {Repo}", timeout,
            async () => (await Checks(sha, ct)) is { } checks && checks.Count >= index ? checks[index - 1] : null, ct);

    /// <summary>Waits for a check run to complete.</summary>
    public Task<CheckRun> AwaitCompleted(long id, TimeSpan timeout, CancellationToken ct) =>
        Poll.Until($"check run {id} in {Repo} to complete", timeout,
            async () => await Check(id, ct) is { Completed: true } check ? check : null, ct);

    /// <summary>Waits for a check run to be linked to its target run.</summary>
    public Task<CheckRun> AwaitLinked(long id, TimeSpan timeout, CancellationToken ct) =>
        Poll.Until($"check run {id} in {Repo} to name its target run", timeout,
            async () => await Check(id, ct) is { RunId: not null } check ? check : null, ct);

    /// <summary>
    /// Waits until the newest <c>main-watcher</c> check run on <c>main</c>'s head has completed as <paramref name="conclusion"/>:
    /// the worker tests every new head, so this is how a scenario knows a push has been judged.
    /// </summary>
    public async Task<CheckRun> AwaitHeadResult(string sha, string conclusion, TimeSpan timeout, CancellationToken ct)
    {
        var check = await Poll.Until($"a completed check run on {Short(sha)} in {Repo}", timeout,
            async () => (await Checks(sha, ct)).LastOrDefault() is { Completed: true } done ? done : null, ct);
        if (check.Conclusion != conclusion)
            throw new ScenarioFailure($"{Repo} {Short(sha)}: expected {conclusion}, got {check}: {Excerpt(check.Summary)}");
        return check;
    }

    // ---- issues ----

    /// <summary>Locks: <c>main-broken</c> issues written by the <c>main-watcher</c> App, newest first.</summary>
    public async Task<List<Issue>> Locks(CancellationToken ct, string state = "all") =>
        (await github.All($"repos/{Repo}/issues?labels=main-broken&state={state}&sort=created&direction=desc", ct, maxPages: 2))
            .Where(i => i["pull_request"] is null).Select(global::MainWatcher.Scenarios.Sandbox.Issue.From).Where(i => i.Author == SandboxOrg.BotLogin).ToList();

    public async Task<Issue> Issue(int number, CancellationToken ct) => global::MainWatcher.Scenarios.Sandbox.Issue.From((await github.Get($"repos/{Repo}/issues/{number}", ct))!);

    public async Task<List<Comment>> Comments(int number, CancellationToken ct) =>
        (await github.All($"repos/{Repo}/issues/{number}/comments", ct)).Select(Comment.From).ToList();

    /// <summary>Waits for a lock created at or after <paramref name="since"/>.</summary>
    public Task<Issue> AwaitNewLock(DateTimeOffset since, TimeSpan timeout, CancellationToken ct) =>
        Poll.Until($"a new lock on {Repo}", timeout,
            async () => (await Locks(ct)).FirstOrDefault(i => i.CreatedAt >= since.AddSeconds(-5)), ct);

    public async Task CloseIssue(int number, CancellationToken ct, string? comment = null)
    {
        if (comment is not null) await github.Post($"repos/{Repo}/issues/{number}/comments", new { body = comment }, ct);
        await github.Patch($"repos/{Repo}/issues/{number}", new { state = "closed" }, ct);
    }

    // ---- pull requests and the merge queue ----

    /// <summary>Opens a pull request adding one file under <see cref="ProbeFolder"/>, or the files given.</summary>
    public async Task<PullRequest> OpenPullRequest(string name, CancellationToken ct, string[]? labels = null,
        IReadOnlyDictionary<string, string?>? files = null)
    {
        var branch = $"scenario/{name}-{DateTimeOffset.UtcNow:HHmmss}";
        var title = $"Scenario probe {name}";
        var head = await Commit(files ?? new Dictionary<string, string?> { [$"{ProbeFolder}/{name}.md"] = $"Probe {name}\n" },
            title, ct, branch);
        var pr = (await github.Post($"repos/{Repo}/pulls", new { title, head = branch, @base = "main", body = "Opened by the scenario suite." }, ct))!;
        var number = pr["number"]!.GetValue<int>();
        if (labels is { Length: > 0 }) await AddLabels(number, labels, ct);
        return new(number, pr["node_id"]!.GetValue<string>(), branch, head, title);
    }

    public Task AddLabels(int number, string[] labels, CancellationToken ct) =>
        github.Post($"repos/{Repo}/issues/{number}/labels", new { labels }, ct);

    /// <summary>
    /// Adds a pull request to the merge queue, once its own required checks have passed, which on a pull request they do at
    /// once. A pull request cannot be queued before that.
    /// </summary>
    public async Task Enqueue(PullRequest pr, CancellationToken ct)
    {
        await Poll.True($"the required checks of #{pr.Number} in {Repo}", TimeSpan.FromMinutes(8), async () =>
        {
            var runs = await github.All($"repos/{Repo}/commits/{pr.HeadSha}/check-runs?filter=latest", ct, "check_runs", 1);
            string? Conclusion(string name) => runs.FirstOrDefault(r => r["name"]?.GetValue<string>() == name)?["conclusion"]?.GetValue<string>();
            return Conclusion("main-watcher-gate") == "success" && Conclusion("sandbox-slow-check") == "success";
        }, ct, TimeSpan.FromSeconds(10));
        await github.GraphQL("mutation($id:ID!){enqueuePullRequest(input:{pullRequestId:$id}){mergeQueueEntry{id}}}", new { id = pr.NodeId }, ct);
    }

    /// <summary>Whether a pull request has merged, is still queued, or neither; and when it merged.</summary>
    public async Task<(bool Merged, bool Queued, DateTimeOffset? MergedAt, string State)> QueueState(PullRequest pr, CancellationToken ct)
    {
        var data = await github.GraphQL(
            "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){state merged mergedAt mergeQueueEntry{state}}}}",
            new { owner = Repo.Split('/')[0], name = Name, number = pr.Number }, ct);
        var node = data["repository"]!["pullRequest"]!;
        return (node["merged"]!.GetValue<bool>(), node["mergeQueueEntry"] is JsonObject,
            CheckRun.Time(node["mergedAt"]), node["state"]!.GetValue<string>());
    }

    /// <summary>Waits for a queued pull request to merge, and returns when it did.</summary>
    public Task<DateTimeOffset> AwaitMerged(PullRequest pr, TimeSpan timeout, CancellationToken ct) =>
        Poll.Value($"#{pr.Number} in {Repo} to merge", timeout, async () =>
        {
            var state = await QueueState(pr, ct);
            if (!state.Merged && !state.Queued && state.State == "OPEN")
                throw new ScenarioFailure($"#{pr.Number} in {Repo} left the merge queue without merging");
            return state.Merged ? state.MergedAt : null;
        }, ct, TimeSpan.FromSeconds(15));

    /// <summary>Waits for a queued pull request to be removed from the queue without merging.</summary>
    public Task AwaitRemovedFromQueue(PullRequest pr, TimeSpan timeout, CancellationToken ct) =>
        Poll.True($"#{pr.Number} in {Repo} to leave the merge queue unmerged", timeout, async () =>
        {
            var state = await QueueState(pr, ct);
            if (state.Merged) throw new ScenarioFailure($"#{pr.Number} in {Repo} merged, but the gate should have removed it");
            return !state.Queued;
        }, ct, TimeSpan.FromSeconds(15));

    public async Task ClosePullRequest(int number, CancellationToken ct)
    {
        var pr = (await github.Get($"repos/{Repo}/pulls/{number}", ct))!;
        if (pr["state"]!.GetValue<string>() == "open") await github.Patch($"repos/{Repo}/pulls/{number}", new { state = "closed" }, ct);
        var branch = pr["head"]!["ref"]!.GetValue<string>();
        try { await github.Delete($"repos/{Repo}/git/refs/heads/{branch}", ct); }
        catch (GitHubException) { }
    }

    public async Task<List<int>> OpenPullRequests(CancellationToken ct) =>
        (await github.All($"repos/{Repo}/pulls?state=open", ct)).Select(p => p["number"]!.GetValue<int>()).ToList();

    // ---- workflow runs ----

    /// <summary>Runs of a workflow in this repo created at or after <paramref name="since"/>, newest first.</summary>
    public async Task<List<Run>> Runs(string workflow, DateTimeOffset since, CancellationToken ct, string? eventName = null) =>
        (await github.All($"repos/{Repo}/actions/workflows/{workflow}/runs?created=>={since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}"
            + (eventName is null ? "" : $"&event={eventName}"), ct, "workflow_runs", 3)).Select(global::MainWatcher.Scenarios.Sandbox.Run.From).ToList();

    public async Task<Run> Run(long id, CancellationToken ct) => global::MainWatcher.Scenarios.Sandbox.Run.From((await github.Get($"repos/{Repo}/actions/runs/{id}", ct))!);

    /// <summary>The run's jobs, or null once the run has been deleted.</summary>
    public async Task<List<Job>?> Jobs(long runId, CancellationToken ct, int? attempt = null)
    {
        var path = attempt is null ? $"repos/{Repo}/actions/runs/{runId}/jobs?filter=all" : $"repos/{Repo}/actions/runs/{runId}/attempts/{attempt}/jobs";
        try { return (await github.All(path, ct, "jobs", 2)).Select(Job.From).ToList(); }
        catch (GitHubException e) when (e.Status == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>The <c>main-watcher</c> job of a target run.</summary>
    public async Task<Job?> TestJob(long runId, CancellationToken ct) =>
        (await Jobs(runId, ct))?.SingleOrDefault(j => j.Name.EndsWith("/ main-watcher", StringComparison.Ordinal) || j.Name == "main-watcher");

    /// <summary>Waits until the <c>main-watcher</c> job of a target run satisfies <paramref name="condition"/>.</summary>
    public Task<Job> AwaitTestJob(long runId, string what, Func<Job, bool> condition, TimeSpan timeout, CancellationToken ct, TimeSpan? interval = null) =>
        Poll.Until($"{what} (target run {runId} in {Repo})", timeout,
            async () => await TestJob(runId, ct) is { } job && condition(job) ? job : null, ct, interval);

    public Task<string> JobLog(long jobId, CancellationToken ct) => github.Text($"repos/{Repo}/actions/jobs/{jobId}/logs", ct);

    public Task CancelRun(long runId, CancellationToken ct) => github.Post($"repos/{Repo}/actions/runs/{runId}/cancel", null, ct);

    public Task DeleteRun(long runId, CancellationToken ct) => github.Delete($"repos/{Repo}/actions/runs/{runId}", ct);

    /// <summary>Starts a workflow in this repo, such as <c>sandbox-hold.yml</c>, and returns its run.</summary>
    public async Task<long> Dispatch(string workflow, IReadOnlyDictionary<string, string> inputs, CancellationToken ct) =>
        (await github.Post($"repos/{Repo}/actions/workflows/{workflow}/dispatches",
            new { @ref = "main", inputs, return_run_details = true }, ct))!["workflow_run_id"]!.GetValue<long>();

    /// <summary>The merge-group gate runs for a pull request, newest first.</summary>
    public async Task<List<Run>> GateRuns(int prNumber, DateTimeOffset since, CancellationToken ct) =>
        (await Runs("main-watcher-gate.yml", since, ct, "merge_group"))
            .Where(r => r.HeadBranch.StartsWith($"gh-readonly-queue/main/pr-{prNumber}-", StringComparison.Ordinal)).ToList();

    /// <summary>The gate job of a gate run attempt, and its log.</summary>
    public async Task<(Job Job, string Log)> GateJob(long runId, CancellationToken ct, int? attempt = null)
    {
        var jobs = await Jobs(runId, ct, attempt) ?? throw new ScenarioFailure($"gate run {runId} in {Repo} has gone");
        var gate = jobs.Single(j => j.Name == "main-watcher-gate");
        return (gate, gate.Completed ? await JobLog(gate.Id, ct) : "");
    }

    /// <summary>The files of a run's artifact, by path, or none if the run uploaded no artifact of that name.</summary>
    public async Task<Dictionary<string, string>> Artifact(long runId, string name, CancellationToken ct)
    {
        var files = new Dictionary<string, string>();
        var artifact = (await github.All($"repos/{Repo}/actions/runs/{runId}/artifacts", ct, "artifacts", 1))
            .FirstOrDefault(a => a["name"]?.GetValue<string>() == name);
        if (artifact is null) return files;
        var zip = await github.Bytes($"repos/{Repo}/actions/artifacts/{artifact["id"]!.GetValue<long>()}/zip", ct);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        foreach (var entry in archive.Entries.Where(e => e.Length > 0))
        {
            using var reader = new StreamReader(entry.Open());
            files[entry.FullName] = await reader.ReadToEndAsync(ct);
        }
        return files;
    }

    public static string Short(string sha) => sha[..Math.Min(7, sha.Length)];

    public static string Excerpt(string text) => text.Length > 400 ? text[..400] + "…" : text;
}
