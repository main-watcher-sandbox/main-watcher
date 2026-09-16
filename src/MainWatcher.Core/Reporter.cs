using System.Globalization;

namespace MainWatcher.Core;

/// <summary>
/// Reports the ADR-013 outcome with ADR-007 CTRF failure details. The issue side is written first
/// and the check run is completed last: red opens the lock issue, green closes it (ADR-004).
/// </summary>
public sealed class Reporter(IGitHubGateway github, Alerts? alerts = null, string botLogin = Reporter.DefaultBotLogin,
    Func<DateTimeOffset>? clock = null)
{
    public const string DefaultBotLogin = "main-watcher[bot]";
    public const string LockLabel = "main-broken";
    /// <summary><c>lock_lease</c> (ADR-014). Renewal and configuration come with the lease work (#19).</summary>
    public static readonly TimeSpan LockLease = TimeSpan.FromHours(4);
    /// <summary>GitHub rejects issue bodies over 65536 characters, and a rejected body would leave <c>main</c> unlocked.</summary>
    public const int MaxIssueBody = 60000;
    const int MaxMentions = 50;
    const int FailureBudget = 20000;
    const int PushBudget = 25000;
    const int MaxFieldLength = 200;

    /// <summary>Alerts that could not be raised. They never block the lock or the check run.</summary>
    public List<string> AlertFailures { get; } = [];

    /// <summary>One entry per push list collected, for the job summary (ADR-003 walk-back length).</summary>
    public List<WalkBack> WalkBacks { get; } = [];

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
            if (outcome.Conclusion == "failure")
            {
                var lockIssue = (await Locks(repo, ct)).FirstOrDefault();
                if (lockIssue is null) lockIssue = await OpenLock(target, check, reports, runUrl, ct);
                // One comment per later failing run. It mentions nobody; replay against its marker comes with #12.
                else await github.Comment(repo, lockIssue.Number,
                    $"Tests failed again on `main` at {Commit(repo, check.Sha)}.\n\n**Failing tests**\n\n{FailureList(reports, FailureBudget)}\n\n"
                    + $"[Target run]({runUrl})\n\n<!-- main-watcher check={check.Id} -->", ct);
                summary += $"\n\nLock issue: {lockIssue.Url}\n\n" + FailureList(reports, FailureBudget);
            }
            else
            {
                foreach (var open in await Locks(repo, ct))
                {
                    await github.Comment(repo, open.Number,
                        $"Tests passed on {Commit(repo, check.Sha)} ([target run]({runUrl})), so `main` is green again. Closing the lock." +
                        $"\n\n<!-- main-watcher check={check.Id} -->", ct);
                    await github.Close(repo, open.Number, ct);
                }
                if (reports.Failures.Count > 0) summary += "\n\nWarning: CTRF reports failures despite a successful test step.";
            }
        }
        // GitHub caps check output at 65535 bytes. Conservatively bound UTF-16 length.
        if (summary.Length > 15000) summary = summary[..15000] + "\n\nOutput truncated; see target run.";
        await github.Complete(repo, check.Id, outcome.Conclusion, summary, ct);
        return true;
    }

    /// <summary>Open <c>main-broken</c> issues authored by the App, oldest first. Anyone else's issue is not a lock.</summary>
    async Task<Issue[]> Locks(string repo, CancellationToken ct) =>
        (await github.OpenIssues(repo, LockLabel, ct))
        .Where(i => i.Author == botLogin && i.AuthorType == "Bot").OrderBy(i => i.Number).ToArray();

    async Task<Issue> OpenLock(Target target, CheckRun check, CtrfResult reports, string runUrl, CancellationToken ct)
    {
        IReadOnlyList<string> mentions = target.Notify.Length > 0 ? target.Notify.Select(Mentions.Normalize).ToArray() : await CodeOwners(target.Repo, ct);
        // GitHub notifies at most 50 mentions per issue; the cap also bounds the body.
        mentions = mentions.Take(MaxMentions).ToArray();
        var pushes = await PushList.Collect(github, target.Repo, check.Sha, ct);
        WalkBacks.Add(new(check.Id, pushes.CommitsChecked, pushes.Source));
        var leaseUntil = (clock ?? (() => DateTimeOffset.UtcNow))().Add(LockLease).UtcDateTime;
        var body = (mentions.Count > 0 ? string.Join(" ", mentions) + "\n\n" : "")
            + $"Main Watcher tests failed on `main` at {Commit(target.Repo, check.Sha)}.\n\n"
            + "**Failing tests**\n\n" + FailureList(reports, FailureBudget) + "\n\n"
            + $"[Target run]({runUrl})\n\n"
            + "**Pushes since the last green run**\n\n" + PushTable(target.Repo, check.Sha, pushes, PushBudget) + "\n\n"
            + $"While this issue is open, the merge queue accepts only pull requests labelled `fixes-main`. "
            + "A green Main Watcher run on `main` closes it. Closing it by hand overrides the lock.\n\n"
            + "<!-- main-watcher " + (pushes.Green is { } green ? $"last_green={green.Sha} " : "") + $"first_red={check.Sha} lease_until={leaseUntil.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} "
            + $"reported_check={check.Id} reported_sha={check.Sha} -->";
        var issue = await github.CreateIssue(target.Repo, $"main is broken: tests failed on {Short(check.Sha)}", body, LockLabel, ct);
        if (mentions.Count == 0)
            await Alert($"Lock issues on {target.Repo} mention nobody",
                $"Lock {issue.Url} mentions nobody: `{target.Repo}` has no `notify` list in targets.yml and no `*` rule with owners in CODEOWNERS. "
                + "Configure `notify` for this target.", ct);
        return issue;
    }

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
            + (result.Truncated ? $"\n\nOnly the newest {PushList.Limit} pushes were read; older ones may also be relevant." : "");
    }

    static string ShaRange(string repo, Push push)
    {
        static bool Real(string sha) => sha.Length == 40 && sha.All(char.IsAsciiHexDigit) && sha.Any(c => c != '0');
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
