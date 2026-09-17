using System.Globalization;
using System.Text.RegularExpressions;

namespace MainWatcher.Core;

/// <summary>
/// Reports the ADR-013 outcome with ADR-007 CTRF failure details. The issue side is written first
/// and the check run is completed last: red opens the lock issue, green closes it (ADR-004).
/// Each write is checked against the hidden markers first, so a report that stopped part-way is
/// replayed without repeating a write or undoing a human override (ADR-013).
/// </summary>
public sealed class Reporter(IGitHubGateway github, Alerts? alerts = null, string botLogin = Reporter.DefaultBotLogin,
    Func<DateTimeOffset>? clock = null, Action<string>? afterWrite = null)
{
    public const string DefaultBotLogin = "main-watcher[bot]";
    public const string LockLabel = "main-broken";
    /// <summary><c>lock_lease</c> (ADR-014). Renewal and configuration come with the lease work (#19).</summary>
    public static readonly TimeSpan LockLease = TimeSpan.FromHours(4);
    /// <summary><c>reconcile_lookback</c> (ADR-015): closed locks updated within it are read before any write.</summary>
    public static readonly TimeSpan ReconcileLookback = TimeSpan.FromDays(30);
    /// <summary>GitHub rejects issue bodies over 65536 characters, and a rejected body would leave <c>main</c> unlocked.</summary>
    public const int MaxIssueBody = 60000;
    const int MaxMentions = 50;
    const int FailureBudget = 20000;
    const int PushBudget = 25000;
    const int MaxFieldLength = 200;
    /// <summary>A comment for a check is never older than its check run, give or take clock differences.</summary>
    static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    static readonly Regex Marker = new(@"<!--\s*main-watcher\s(?<fields>.*?)-->", RegexOptions.Singleline);

    /// <summary>Alerts that could not be raised. They never block the lock or the check run.</summary>
    public List<string> AlertFailures { get; } = [];

    /// <summary>One entry per push list collected, for the job summary (ADR-003 walk-back length).</summary>
    public List<WalkBack> WalkBacks { get; } = [];

    DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();

    public async Task<bool> Report(Target target, CheckRun check, CancellationToken ct)
    {
        var repo = target.Repo;
        if (check.Status == "completed" || !long.TryParse(check.ExternalId, out var runId)) return false;
        var outcome = Outcomes.Read(await github.Jobs(repo, runId, ct));
        if (outcome is null) return false;
        var runUrl = $"https://github.com/{repo}/actions/runs/{runId}";
        var summary = outcome.Description + $"\n\n[Target run]({runUrl})";
        if (outcome.Conclusion is "success" or "failure")
        {
            var reports = await github.Reports(repo, runId, ct);
            var locks = await Locks(repo, ct);
            if (outcome.Conclusion == "failure") summary += await ReportRed(target, check, locks, reports, runUrl, ct);
            else
            {
                foreach (var open in locks.Open)
                {
                    if (!await locks.Names(open, check, ct))
                        await Write("comment", github.Comment(repo, open.Number,
                            $"Tests passed on {Commit(repo, check.Sha)} ([target run]({runUrl})), so `main` is green again. Closing the lock." +
                            $"\n\n<!-- main-watcher check={check.Id} closed=green -->", ct));
                    await Write("close", github.Close(repo, open.Number, "completed", ct));
                }
                if (reports.Failures.Count > 0) summary += "\n\nWarning: CTRF reports failures despite a successful test step.";
            }
        }
        // GitHub caps check output at 65535 bytes. Conservatively bound UTF-16 length.
        if (summary.Length > 15000) summary = summary[..15000] + "\n\nOutput truncated; see target run.";
        await github.Complete(repo, check.Id, outcome.Conclusion, summary, ct);
        return true;
    }

    async Task<string> ReportRed(Target target, CheckRun check, LockSet locks, CtrfResult reports, string runUrl, CancellationToken ct)
    {
        var repo = target.Repo;
        var failures = FailureList(reports, FailureBudget);
        // Written before and closed since, by a human or a green run: nothing is reopened or created.
        if (await locks.Carrier(check, ct) is { State: "closed" } closed)
            return $"\n\nThis result was already written to lock issue {closed.Url}, which has been closed since, so no lock was opened.\n\n" + failures;
        var open = locks.Open.FirstOrDefault();
        if (open is null)
        {
            var latest = locks.Latest;
            var overridden = latest is { State: "closed" } && !IsApp(await github.ClosedBy(repo, latest.Number, ct));
            // An override covers its commit: only a failure on a different commit locks again (ADR-004).
            if (overridden && Field(latest!.Body, "reported_sha") == check.Sha)
                return $"\n\nThe lock for this commit, {latest.Url}, was closed by hand (an override), so no lock was opened. "
                    + "A failure on a different commit locks again.\n\n" + failures;
            open = await OpenLock(target, check, reports, runUrl, overridden ? latest : null, ct);
        }
        else if (Field(open.Body, "reported_check") != Id(check))
        {
            // The comment first, then the marker: a marker naming this check means both writes were made.
            if (!await locks.Names(open, check, ct))
                await Write("comment", github.Comment(repo, open.Number,
                    $"Tests failed again on `main` at {Commit(repo, check.Sha)}.\n\n**Failing tests**\n\n{failures}\n\n"
                    + $"[Target run]({runUrl})\n\n<!-- main-watcher check={check.Id} -->", ct));
            await Write("update", github.EditBody(repo, open.Number, WithReported(open.Body ?? "", check), ct));
        }
        // This check opened the lock, and the report may have stopped before the alert that follows it.
        else if (Field(open.Body, "first_red") == check.Sha && (await MentionList(target, ct)).Count == 0)
            await NobodyMentioned(target, open, ct);
        return $"\n\nLock issue: {open.Url}\n\n" + failures;
    }

    /// <summary>
    /// Posts the ADR-004 override comment, naming who closed it, on each App lock closed by hand and updated within
    /// <see cref="ReconcileLookback"/>. A lock the App closed, or one with a closing comment from the App, is skipped.
    /// Returns the number of comments posted.
    /// </summary>
    public async Task<int> NoteOverrides(Target target, CancellationToken ct)
    {
        var repo = target.Repo;
        var posted = 0;
        foreach (var issue in (await github.Issues(repo, LockLabel, Now - ReconcileLookback, ct))
            .Where(i => IsApp(i) && i.State == "closed").OrderBy(i => i.Number))
        {
            if ((await github.Comments(repo, issue.Number, null, ct)).Any(c => IsApp(c) && Field(c.Body, "closed") is not null)) continue;
            var closer = await github.ClosedBy(repo, issue.Number, ct);
            if (IsApp(closer)) continue;
            var sha = Field(issue.Body, "reported_sha");
            await Write("override", github.Comment(repo, issue.Number,
                $"{(closer is null ? "Someone" : $"`{closer.Login}`")} closed this lock by hand. That is an override: the merge queue "
                + $"accepts every pull request again, but `main` is still red{(sha is not null && IsSha(sha) ? $" at {Commit(repo, sha)}" : "")}. "
                + "Main Watcher never reopens this issue; it opens a new lock when tests fail on a different commit."
                + "\n\n<!-- main-watcher closed=override -->", ct));
            posted++;
        }
        return posted;
    }

    async Task Write(string name, Task write)
    {
        await write;
        afterWrite?.Invoke(name);
    }

    bool IsApp(Issue issue) => issue.Author == botLogin && issue.AuthorType == "Bot";
    bool IsApp(IssueComment comment) => comment.Author == botLogin && comment.AuthorType == "Bot";
    bool IsApp(Account? account) => account is { Type: "Bot" } && account.Login == botLogin;

    /// <summary>
    /// The App's locks: every open one, and every one updated within <see cref="ReconcileLookback"/>, oldest first.
    /// If more than one is open, the oldest is kept and the newer ones are closed as duplicates (ADR-013).
    /// </summary>
    async Task<LockSet> Locks(string repo, CancellationToken ct)
    {
        // An open lock counts even when nothing has updated it within the lookback. The later read wins on state.
        var issues = (await github.OpenIssues(repo, LockLabel, ct)).Concat(await github.Issues(repo, LockLabel, Now - ReconcileLookback, ct))
            .Where(IsApp).GroupBy(i => i.Number).Select(g => g.Last()).OrderBy(i => i.Number).ToList();
        var locks = new LockSet(github, IsApp, repo, issues);
        foreach (var issue in issues.Where(i => i is { State: "closed", StateReason: "duplicate" }))
            if (await locks.MarkedDuplicate(issue, ct)) locks.Duplicates.Add(issue.Number);
        var open = issues.Where(i => i.State == "open").ToArray();
        foreach (var duplicate in open.Skip(1))
        {
            if (!await locks.MarkedDuplicate(duplicate, ct))
                await Write("comment", github.Comment(repo, duplicate.Number,
                    $"Closing as a duplicate of #{open[0].Number}, the older lock.\n\n<!-- main-watcher closed=duplicate duplicate_of={open[0].Number} -->", ct));
            await Write("close", github.Close(repo, duplicate.Number, "duplicate", ct));
            issues[issues.IndexOf(duplicate)] = duplicate with { State = "closed", StateReason = "duplicate" };
            locks.Duplicates.Add(duplicate.Number);
        }
        return locks;
    }

    /// <summary>The App's locks during one report. Each issue's comments are read at most once.</summary>
    sealed class LockSet(IGitHubGateway github, Func<IssueComment, bool> isApp, string repo, List<Issue> issues)
    {
        readonly Dictionary<int, IReadOnlyList<IssueComment>> comments = [];

        /// <summary>Locks the App closed as duplicates. They never carry a result.</summary>
        public HashSet<int> Duplicates { get; } = [];
        IEnumerable<Issue> Current => issues.Where(i => !Duplicates.Contains(i.Number));
        public IEnumerable<Issue> Open => Current.Where(i => i.State == "open");
        public Issue? Latest => Current.LastOrDefault();

        async Task<IEnumerable<IssueComment>> AppComments(Issue issue, CancellationToken ct)
        {
            if (!comments.TryGetValue(issue.Number, out var list))
                comments[issue.Number] = list = await github.Comments(repo, issue.Number, null, ct);
            return list.Where(isApp);
        }

        public async Task<bool> MarkedDuplicate(Issue issue, CancellationToken ct) =>
            (await AppComments(issue, ct)).Any(c => Field(c.Body, "closed") == "duplicate");

        /// <summary>Whether one of the App's comments on <paramref name="issue"/> carries this check's marker.</summary>
        public async Task<bool> Names(Issue issue, CheckRun check, CancellationToken ct) =>
            (await AppComments(issue, ct)).Any(c => Field(c.Body, "check") == Id(check));

        /// <summary>The lock this check was already written to, through the body marker or a comment.</summary>
        public async Task<Issue?> Carrier(CheckRun check, CancellationToken ct)
        {
            foreach (var issue in Current.Reverse())
            {
                if (Field(issue.Body, "reported_check") == Id(check)) return issue;
                // A comment for this check would have updated the issue after the check run started.
                if (issue.UpdatedAt is { } updated && updated < check.StartedAt - ClockSkew) continue;
                if (await Names(issue, check, ct)) return issue;
            }
            return null;
        }
    }

    static string Id(CheckRun check) => check.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The last value of <paramref name="name"/> in the hidden markers of <paramref name="text"/>.</summary>
    static string? Field(string? text, string name) => Marker.Matches(text ?? "")
        .SelectMany(m => m.Groups["fields"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(f => f.StartsWith(name + "=", StringComparison.Ordinal)).Select(f => f[(name.Length + 1)..]).LastOrDefault();

    /// <summary>Points the body's marker at <paramref name="check"/>, keeping its other fields, such as <c>lease_until</c>.</summary>
    static string WithReported(string body, CheckRun check)
    {
        var reported = $"reported_check={check.Id} reported_sha={check.Sha}";
        if (Marker.Matches(body) is not { Count: > 0 } markers) return body + $"\n\n<!-- main-watcher {reported} -->";
        var marker = markers[^1];
        var fields = marker.Groups["fields"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(f => !f.StartsWith("reported_check=", StringComparison.Ordinal) && !f.StartsWith("reported_sha=", StringComparison.Ordinal));
        return body[..marker.Index] + $"<!-- main-watcher {string.Join(" ", fields.Append(reported))} -->" + body[(marker.Index + marker.Length)..];
    }

    async Task<Issue> OpenLock(Target target, CheckRun check, CtrfResult reports, string runUrl, Issue? overridden, CancellationToken ct)
    {
        var mentions = await MentionList(target, ct);
        var pushes = await PushList.Collect(github, target.Repo, check.Sha, ct);
        WalkBacks.Add(new(check.Id, pushes.CommitsChecked, pushes.Source));
        var leaseUntil = Now.Add(LockLease).UtcDateTime;
        var body = (mentions.Count > 0 ? string.Join(" ", mentions) + "\n\n" : "")
            + $"Main Watcher tests failed on `main` at {Commit(target.Repo, check.Sha)}.\n\n"
            + "**Failing tests**\n\n" + FailureList(reports, FailureBudget) + "\n\n"
            + $"[Target run]({runUrl})\n\n"
            // ADR-004: a failure after an override opens a new issue, linked to the previous one.
            + (overridden is null ? "" : $"The previous lock, {overridden.Url}, was closed by hand.\n\n")
            + "**Pushes since the last green run**\n\n" + PushTable(target.Repo, check.Sha, pushes, PushBudget) + "\n\n"
            + $"While this issue is open, the merge queue accepts only pull requests labelled `fixes-main`. "
            + "A green Main Watcher run on `main` closes it. Closing it by hand overrides the lock.\n\n"
            + "<!-- main-watcher " + (pushes.Green is { } green ? $"last_green={green.Sha} " : "") + $"first_red={check.Sha} lease_until={leaseUntil.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} "
            + $"reported_check={check.Id} reported_sha={check.Sha} -->";
        var issue = await github.CreateIssue(target.Repo, $"main is broken: tests failed on {Short(check.Sha)}", body, LockLabel, ct);
        afterWrite?.Invoke("create");
        if (mentions.Count == 0) await NobodyMentioned(target, issue, ct);
        return issue;
    }

    async Task<IReadOnlyList<string>> MentionList(Target target, CancellationToken ct) =>
        // GitHub notifies at most 50 mentions per issue; the cap also bounds the body.
        (target.Notify.Length > 0 ? target.Notify.Select(Mentions.Normalize).ToArray() : await CodeOwners(target.Repo, ct)).Take(MaxMentions).ToArray();

    Task NobodyMentioned(Target target, Issue issue, CancellationToken ct) => Alert($"Lock issues on {target.Repo} mention nobody",
        $"Lock {issue.Url} mentions nobody: `{target.Repo}` has no `notify` list in targets.yml and no `*` rule with owners in CODEOWNERS. "
        + "Configure `notify` for this target.", ct);

    /// <summary>Says how the list was bounded, then lists pushes newest first within <paramref name="budget"/> characters.</summary>
    static string PushTable(string repo, string failingSha, PushListResult result, int budget)
    {
        var green = result.Green;
        if (result.Source == PushSource.Unavailable)
            return "Push list unavailable: the repository activity could not be read. " + (green is null
                ? $"[Commits on `main` up to `{Short(failingSha)}`](https://github.com/{repo}/commits/{failingSha})"
                : $"[Compare `{Short(green.Sha)}...{Short(failingSha)}`](https://github.com/{repo}/compare/{green.Sha}...{failingSha})");
        var source = result.Source switch
        {
            PushSource.SinceGreen => $"Since the last green commit, {Commit(repo, green!.Sha)}.",
            PushSource.AfterGreenCheck => $"The last green commit, `{Short(green!.Sha)}`, is not among the newest {PushList.Limit} commits of `main` "
                + $"(it may have been force-pushed away), so this lists activity after its check run started at {Time(green.StartedAt)} UTC.",
            _ => $"No green Main Watcher run was found, so this lists the last {PushList.Limit} pushes."
        };
        if (result.Pushes.Count == 0) return source + "\n\nNo pushes found.";
        var lines = new List<string> { "| Time (UTC) | Pusher | Type | Before → after | Commits |", "| --- | --- | --- | --- | --- |" };
        var length = lines.Sum(l => l.Length + 1);
        foreach (var (push, commits) in result.Pushes)
        {
            var type = push.Type switch
            {
                "push" => "push",
                "force_push" => "force push",
                "pr_merge" => "PR merge",
                "merge_queue_merge" => "merge-queue merge",
                var other => Escape(Clip(other.Replace('_', ' ')))
            };
            // A plain name: logins cannot contain @, and Escape would neutralise one anyway.
            var line = $"| {Time(push.Timestamp)} | {Escape(Clip(push.Actor ?? "unknown")).Replace("|", "\\|")} | {type} | "
                + $"{ShaRange(repo, push)} | {commits?.ToString(CultureInfo.InvariantCulture) ?? "?"} |";
            if (length + line.Length + 1 > budget) break;
            lines.Add(line);
            length += line.Length + 1;
        }
        var more = result.Pushes.Count - (lines.Count - 2);
        return source + "\n\n" + string.Join("\n", lines)
            + (more > 0 ? $"\n\n…and {more} more; see the repository activity." : "")
            + (result.Incomplete ? "\n\nThe repository activity read does not reach back far enough; older pushes may also be relevant." : "");
    }

    static bool IsSha(string sha) => sha.Length == 40 && sha.All(char.IsAsciiHexDigit);

    static string ShaRange(string repo, Push push)
    {
        static bool Real(string sha) => IsSha(sha) && sha.Any(c => c != '0');
        var range = $"{(Real(push.Before) ? $"`{Short(push.Before)}`" : "none")} → {(Real(push.After) ? $"`{Short(push.After)}`" : "none")}";
        return Real(push.Before) && Real(push.After) ? $"[{range}](https://github.com/{repo}/compare/{push.Before}...{push.After})" : range;
    }

    static string Time(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    async Task<IReadOnlyList<string>> CodeOwners(string repo, CancellationToken ct)
    {
        // GitHub uses the first CODEOWNERS file it finds, in this order.
        foreach (var path in Mentions.CodeOwnersPaths)
            if (await github.File(repo, path, ct) is { } text) return Mentions.StarOwners(text);
        return [];
    }

    async Task Alert(string title, string body, CancellationToken ct)
    {
        if (alerts is null) { AlertFailures.Add($"{title}: no alert sink configured"); return; }
        try { await alerts.Raise(title, body, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested) { AlertFailures.Add($"{title}: {e.Message}"); }
    }

    /// <summary>Lists failures within <paramref name="budget"/> characters, so any valid CTRF report fits in an issue body.</summary>
    static string FailureList(CtrfResult reports, int budget)
    {
        if (!reports.Known || reports.Failures.Count == 0) return "failing tests unknown";
        var lines = new List<string>();
        var length = 0;
        foreach (var f in reports.Failures)
        {
            var line = $"- {Escape(Clip(f.Name))} ({Escape(Clip(f.Suite))}): {Escape(Clip(f.Message))}";
            if (length + line.Length + 1 > budget) break;
            lines.Add(line);
            length += line.Length + 1;
        }
        var more = reports.Failures.Count - lines.Count;
        return string.Join("\n", lines) + (more > 0 ? $"\n\n…and {more} more; see the target run." : "");
    }

    // Clipped before escaping, so an escaped field is at most a few times this length.
    static string Clip(string text) => text.Length > MaxFieldLength ? text[..MaxFieldLength] + "…" : text;

    static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
    static string Commit(string repo, string sha) => $"[`{Short(sha)}`](https://github.com/{repo}/commit/{sha})";

    // HTML-encoding also stops test output from opening a hidden marker; &#64; stops it mentioning anyone.
    static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text)
        .Replace("\r", " ").Replace("\n", " ").Replace("`", "\\`").Replace("*", "\\*").Replace("[", "\\[").Replace("@", "&#64;");
}
