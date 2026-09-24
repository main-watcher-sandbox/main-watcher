using System.Globalization;

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
    /// <summary>The production App. The sandbox's is <c>main-watcher[bot]</c>, a name the sandbox org already held.</summary>
    public const string DefaultBotLogin = "actium-main-watcher[bot]";
    public const string LockLabel = "main-broken";
    /// <summary><c>reconcile_lookback</c> (ADR-015): closed locks updated within it are read before any write.</summary>
    public static readonly TimeSpan ReconcileLookback = TimeSpan.FromDays(30);
    /// <summary>GitHub rejects issue bodies over 65536 characters, and a rejected body would leave <c>main</c> unlocked.</summary>
    public const int MaxIssueBody = 60000;
    const int MaxMentions = 50;
    const int FailureBudget = 20000;
    const int PushBudget = 25000;
    /// <summary>A comment for a check is never older than its check run, give or take clock differences.</summary>
    static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Alerts that could not be raised. They never block the lock or the check run.</summary>
    public List<string> AlertFailures { get; } = [];

    /// <summary>One entry per push list collected, for the job summary (ADR-003 walk-back length).</summary>
    public List<WalkBack> WalkBacks { get; } = [];

    /// <summary>
    /// The locks this Reporter has just created, for the queue sweep that follows in the same cycle (ADR-016). It is handed
    /// over rather than looked up because GitHub's issue list does not show a new issue at once: in the sandbox, the list read
    /// a second after the lock was created did not hold it, so the sweep would find nothing to do and only the next cycle,
    /// about a minute later, would re-run the gates of the groups already queued.
    /// </summary>
    public List<Issue> Opened { get; } = [];

    DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();

    public async Task<bool> Report(Target target, CheckRun check, CancellationToken ct)
    {
        var repo = target.Repo;
        if (check.Status == "completed" || !long.TryParse(check.ExternalId, out var runId)) return false;
        var outcome = Outcomes.Read(await github.Jobs(repo, runId, ct), Now);
        // A job GitHub is still writing down is read again next cycle; the check run stays in progress (ADR-019).
        if (outcome is null or { Kind: OutcomeKind.StepsNotFinal }) return false;
        var runUrl = $"https://github.com/{repo}/actions/runs/{runId}";
        var summary = outcome.Description + $"\n\n[Target run]({runUrl})";
        // The Planner records a stale run's stopping in this same output (ADR-013 point 5). Say so, because the step
        // conclusions alone do not tell a cancelled run from one the target's own code stopped.
        if (Markers.Time(check.Summary, StaleRun.CancelRequested) is { } stopped)
            summary += $"\n\nMain Watcher cancelled this run at {Markers.Stamp(stopped)} after it passed its deadline.";
        if (outcome.Conclusion is "success" or "failure")
        {
            var reports = await github.Reports(repo, runId, ct);
            // Before the lock details, so a long failure list cannot truncate it or its marker away.
            summary += "\n\n" + await Timing(repo, check, reports, ct);
            var locks = await Locks(repo, ct);
            if (outcome.Conclusion == "failure") summary += await ReportRed(target, check, locks, reports, runUrl, ct);
            else
            {
                foreach (var open in locks.Open)
                {
                    if (!await locks.Names(open, check, ct))
                        await Write("comment", github.Comment(repo, open.Number,
                            $"Tests passed on {Commit(repo, check.Sha)} ([target run]({runUrl})), so `main` is green again. Closing the lock." +
                            await Removed(repo, open, ct) +
                            $"\n\n<!-- main-watcher check={check.Id} sha={check.Sha} closed=green -->", ct));
                    await Write("close", github.Close(repo, open.Number, "completed", null, ct));
                }
                if (reports.Failures.Count > 0) summary += "\n\nWarning: CTRF reports failures despite a successful test step.";
            }
        }
        else
        {
            // Infrastructure and contract errors never lock the queue (CQ-5): alert, then complete as neutral. The alert is this
            // result's report, so a failure to raise it leaves the check in progress, to be replayed (ADR-013).
            await ReportNeutral(target, check, outcome, runUrl, ct);
            summary += "\n\nNo lock was opened. The head is tested again after `poll_interval`.";
        }
        // GitHub caps check output at 65535 bytes. Conservatively bound UTF-16 length.
        if (summary.Length > 15000) summary = summary[..15000] + "\n\nOutput truncated; see target run.";
        // Named by what it writes, so the sandbox switch can stop a cycle at the boundary ADR-017 turns on: the moment a
        // neutral result is written, with nothing left for the retest but the rule itself (TS-S18).
        await Write($"check:{outcome.Conclusion}", github.Complete(repo, check.Id, outcome.Conclusion, outcome.Title, summary, ct));
        return true;
    }

    /// <summary>
    /// The check run's timing section (ADR-011). The last green run's time is only for comparison, so check runs that cannot be
    /// read leave the change unknown rather than the report unwritten.
    /// </summary>
    async Task<string> Timing(string repo, CheckRun check, CtrfResult reports, CancellationToken ct)
    {
        if (reports.Timing is null) return TimingSection.Write(repo, reports, null);
        try { return TimingSection.Write(repo, reports, TimingSection.LastGreen(await github.Checks(repo, ct), check)); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or IOException or TaskCanceledException)
        {
            return TimingSection.Write(repo, reports, null, e.Message);
        }
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
            // An override covers its commit: only a failure on a different commit locks again (ADR-004). The commit may be in
            // a comment only, when the report that wrote it stopped before updating the body's marker.
            if (overridden && latest is not null && (await locks.ReportedShas(latest, ct)).Contains(check.Sha))
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
                    + $"[Target run]({runUrl})\n\n<!-- main-watcher check={check.Id} sha={check.Sha} -->", ct));
            await Write("update", github.EditBody(repo, open.Number, WithReported(open.Body ?? "", check), ct));
        }
        // This check opened the lock, and the report may have stopped before the alert that follows it.
        else if (Field(open.Body, "first_red") == check.Sha && (await MentionList(target, ct)).Count == 0)
            await NobodyMentioned(target, open, ct);
        return $"\n\nLock issue: {open.Url}\n\n" + failures;
    }

    /// <summary>
    /// Raises the <c>watcher-infra</c> alert for a neutral outcome (ADR-013) and, for an infrastructure error, "twice in a row"
    /// when the target's previous completed check run was an infrastructure error too, as its output title records. Each alert
    /// names the check in a hidden marker, so a replay of this report does not repeat it. Unlike the "mention nobody" alert,
    /// these are required writes: a failure is thrown, and the check run stays <c>in_progress</c>.
    /// </summary>
    async Task ReportNeutral(Target target, CheckRun check, TestOutcome outcome, string runUrl, CancellationToken ct)
    {
        if (alerts is null) throw new InvalidOperationException("A neutral result cannot be reported without an alert sink.");
        var repo = target.Repo;
        var key = $"<!-- main-watcher check={check.Id} -->";
        var what = $"Check run {check.Id} on {Commit(repo, check.Sha)} in `{repo}` completed as `neutral` and opened no lock "
            + $"([target run]({runUrl})).\n\n{outcome.Description}\n\n";
        await alerts.Raise(Outcomes.AlertTitle(outcome.Kind, repo)!, what + outcome.Kind switch
        {
            OutcomeKind.Unknown => "The target run no longer exists, so its steps cannot be read.",
            OutcomeKind.ContractBroken => $"The Reporter looks for the job `main-watcher` and the steps `{Outcomes.TestStep}` and `{Outcomes.MarkerStep}`, "
                + "each exactly once. Check the reusable workflow tag this target's `main-watcher-tests.yml` uses.",
            _ => "Setup failed or the tests did not finish: for example a restore failure, the test deadline, a timeout, a cancellation or a lost runner."
        } + " The head is tested again after `poll_interval`.", ct, key);
        if (outcome.Kind != OutcomeKind.InfrastructureError) return;
        var previous = (await github.Checks(repo, ct))
            .Where(c => c.Id != check.Id && c.Status == "completed" && c.StartedAt <= check.StartedAt).MaxBy(c => c.StartedAt);
        if (previous is not { Conclusion: "neutral" } || previous.Title != Outcomes.Title(OutcomeKind.InfrastructureError)) return;
        await alerts.Raise($"Infrastructure errors twice in a row on {repo}", what
            + $"The previous check run, {previous.Id} on {Commit(repo, previous.Sha)}, was also an infrastructure error. "
            + "A lasting infrastructure problem, or a restore failure caused by the code, is keeping `main` untested.", ct, key);
    }

    /// <summary>
    /// The locks <see cref="NoteOverrides"/> saw closed, whose closure it has therefore judged. Reconciliation completes only
    /// these (<see cref="Planner.Reconcile"/>): a lock closed by hand later in the same cycle was reconciled and marked
    /// complete, so no cycle was asked for and its override comment waited for an unrelated one (scenario suite, #25).
    /// </summary>
    public IReadOnlySet<int>? ClosedSeen { get; private set; }

    /// <summary>
    /// Posts the ADR-004 comment, naming who closed it, on each App lock closed by someone other than the App and updated
    /// within <see cref="ReconcileLookback"/>, unless it already has one. A lock the App had started to close, after a green run
    /// or as a duplicate, still gets it, worded for that case. Returns the number of comments posted.
    /// </summary>
    public async Task<int> NoteOverrides(Target target, CancellationToken ct)
    {
        var repo = target.Repo;
        var posted = 0;
        var closed = (await github.Issues(repo, LockLabel, Now - ReconcileLookback, ct))
            .Where(i => IsApp(i) && i.State == "closed").OrderBy(i => i.Number).ToArray();
        ClosedSeen = closed.Select(i => i.Number).ToHashSet();
        // A lock marked reconciled=complete has had its closure judged already: reconciliation completes only a lock this
        // step had seen closed. Skipping it spares two calls per closed lock in the lookback on every cycle, which, with a
        // few dozen closed locks, was most of a sandbox cycle's API use (#25, R-13).
        foreach (var issue in closed.Where(i => !Reconciliation.IsComplete(i.Body)))
        {
            var closing = (await github.Comments(repo, issue.Number, null, ct)).Where(IsApp).Select(c => c.Body).ToArray();
            if (closing.Any(c => Field(c, "closed") == "override")) continue;
            var closer = await github.ClosedBy(repo, issue.Number, ct);
            if (IsApp(closer)) continue;
            var who = closer is null ? "Someone" : $"`{closer.Login}`";
            var green = closing.LastOrDefault(c => Field(c, "closed") == "green");
            var duplicate = closing.LastOrDefault(c => Field(c, "closed") == "duplicate");
            var sha = green is not null ? Field(green, "sha") : Field(issue.Body, "reported_sha");
            var at = sha is not null && IsSha(sha) ? $" at {Commit(repo, sha)}" : "";
            var text = green is not null
                ? $"{who} closed this lock by hand after tests had passed on `main`{at}, so it ends as a green run would have closed it."
                : duplicate is not null
                    ? $"{who} closed this lock by hand. It had already been marked a duplicate of #{Field(duplicate, "duplicate_of")}, which decides whether `main` stays locked."
                    : $"{who} closed this lock by hand. That is an override: the merge queue accepts every pull request again, but `main` is still red{at}. "
                        + "Main Watcher never reopens this issue; it opens a new lock when tests fail on a different commit.";
            await Write("override", github.Comment(repo, issue.Number, text + "\n\n<!-- main-watcher closed=override -->", ct));
            posted++;
        }
        return posted;
    }

    /// <summary>
    /// The pull requests the gate removed from the merge queue while this lock was open, for the unlock comment. GitHub does
    /// not re-queue them, so unlocking without naming them is how they are forgotten (R-7); automatic re-queueing is deferred
    /// (§17). Never a required read: a gate run list that cannot be read leaves the comment shorter, not the lock open.
    /// </summary>
    async Task<string> Removed(string repo, Issue issue, CancellationToken ct)
    {
        IReadOnlyList<GateBlock> blocked;
        // A lock GitHub does not date could only be answered with the whole history of the queue, which is not this list.
        if (issue.CreatedAt is not { } since) return "";
        try { blocked = await github.GateBlocks(repo, since, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or IOException or TaskCanceledException)
        {
            return "\n\nThe pull requests the gate removed from the merge queue could not be read: " + Markdown.Escape(e.Message) + ".";
        }
        // A group the queue sweep caught was removed by a re-run of a gate that had already passed, which is why it is named:
        // it was queued before this lock and would have merged onto a red `main` (ADR-016).
        var pulls = blocked.GroupBy(b => b.Pull).OrderBy(g => g.Key)
            .Select(g => (Pull: g.Key, Swept: g.Any(b => b.Attempt > 1))).ToArray();
        if (pulls.Length == 0) return "";
        return "\n\n**Pull requests the gate removed from the merge queue while this lock was open**\n\n"
            + string.Join("\n", pulls.Select(p => $"- #{p.Pull}" + (p.Swept ? " (its gate was re-run because it was queued before this lock)" : "")))
            + "\n\nThe merge queue does not put them back, so re-queue the ones you still want merged.";
    }

    /// <summary>A write the sandbox fault switch can stop the cycle after, by name (TS-S14, TS-S18).</summary>
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
            await Write("close", github.Close(repo, duplicate.Number, "duplicate", open[0].Id, ct));
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

        /// <summary>Commits a lock reported red: its body's <c>reported_sha</c> and the <c>sha</c> of each later-failure comment.</summary>
        public async Task<HashSet<string>> ReportedShas(Issue issue, CancellationToken ct) =>
            (await AppComments(issue, ct)).Where(c => Field(c.Body, "check") is not null && Field(c.Body, "closed") is null)
                .Select(c => Field(c.Body, "sha")).Append(Field(issue.Body, "reported_sha")).OfType<string>().ToHashSet(StringComparer.Ordinal);

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

    static string? Field(string? text, string name) => Markers.Field(text, name);

    /// <summary>Points the body's marker at <paramref name="check"/>, keeping its other fields, such as <c>lease_until</c>.</summary>
    static string WithReported(string body, CheckRun check) =>
        Markers.Set(body, ("reported_check", Id(check)), ("reported_sha", check.Sha));

    async Task<Issue> OpenLock(Target target, CheckRun check, CtrfResult reports, string runUrl, Issue? overridden, CancellationToken ct)
    {
        var mentions = await MentionList(target, ct);
        var pushes = await PushList.Collect(github, target.Repo, check.Sha, ct);
        WalkBacks.Add(new(check.Id, pushes.CommitsChecked, pushes.Source));
        // The lease starts the moment the lock exists, and the Planner renews it on every later cycle (ADR-014).
        var now = Now;
        var leaseUntil = Markers.Stamp(now + target.LockLease);
        var body = (mentions.Count > 0 ? string.Join(" ", mentions) + "\n\n" : "")
            + $"Main Watcher tests failed on `main` at {Commit(target.Repo, check.Sha)}.\n\n"
            + "**Failing tests**\n\n" + FailureList(reports, FailureBudget) + "\n\n"
            + $"[Target run]({runUrl})\n\n"
            // ADR-004: a failure after an override opens a new issue, linked to the previous one.
            + (overridden is null ? "" : $"The previous lock, {overridden.Url}, was closed by hand.\n\n")
            + "**Pushes since the last green run**\n\n" + PushTable(target.Repo, check.Sha, pushes, PushBudget) + "\n\n"
            + $"While this issue is open, the merge queue accepts only pull requests labelled `fixes-main`. "
            + "A green Main Watcher run on `main` closes it. Closing it by hand overrides the lock.\n\n"
            // ADR-016: the sweep obligation is written by the same call that creates the lock, so a crash straight afterwards
            // still leaves it owed. The generation is the lock's own moment; the sweep itself runs later in this cycle.
            + "<!-- main-watcher " + (pushes.Green is { } green ? $"last_green={green.Sha} " : "") + $"first_red={check.Sha} {Lease.Until}={leaseUntil} "
            + $"{QueueSweep.Required}={Markers.Stamp(now)} reported_check={check.Id} reported_sha={check.Sha} -->";
        var issue = await github.CreateIssue(target.Repo, $"main is broken: tests failed on {Short(check.Sha)}", body, LockLabel, ct);
        Opened.Add(issue);
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

    /// <summary>An alert that never blocks the report: a failure is recorded in <see cref="AlertFailures"/>.</summary>
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

    static string Clip(string text) => Markdown.Clip(text);

    static string Short(string sha) => Markdown.Short(sha);
    static string Commit(string repo, string sha) => Markdown.Commit(repo, sha);

    static string Escape(string text) => Markdown.Escape(text);
}
