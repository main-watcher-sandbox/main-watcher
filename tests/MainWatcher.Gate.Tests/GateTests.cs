using System.Net;
using System.Text.Json;
using static MainWatcher.Gate.Tests.FakeGitHub;

namespace MainWatcher.Gate.Tests;

public class GateTests
{
    static readonly DateTimeOffset ValidLease = Now.AddHours(4);

    // A group of PR 1 and PR 2, merged by the queue with the MERGE method.
    static FakeGitHub LockedGroup(DateTimeOffset leaseUntil, string[] pr1Labels, string[] pr2Labels) =>
        new FakeGitHub()
            .LockIssues(Lock(7, leaseUntil))
            .Compare("main", Sha('f'),
                Commit(Sha('1')), Commit(Sha('b'), "Merge pull request #1 from team/fix"),
                Commit(Sha('2')), Commit(Sha('c'), "Merge pull request #2 from team/feature"))
            .CommitPulls(Sha('1'), Pull(1, pr1Labels))
            .CommitPulls(Sha('b'))
            .CommitPulls(Sha('2'), Pull(2, pr2Labels))
            .CommitPulls(Sha('c'));

    [Theory]
    [InlineData("pull_request")]
    [InlineData("push")]
    public async Task Events_other_than_merge_group_always_pass_without_calling_the_API(string eventName)
    {
        var github = new FakeGitHub();

        var verdict = await github.Gate().DecideAsync(new GateEvent(eventName), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Pass, verdict.Outcome);
        Assert.Null(verdict.FailOpenReason);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task No_lock_issue_passes()
    {
        var verdict = await new FakeGitHub().LockIssues().Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Pass, verdict.Outcome);
        Assert.Null(verdict.FailOpenReason);
    }

    // TS-S6: a hand-made main-broken issue does not lock.
    [Theory]
    [InlineData("maintainer", "User")]
    [InlineData("other-app[bot]", "Bot")]
    [InlineData("actium-main-watcher[bot]", "User")]
    [InlineData("main-watcher", "User")]
    public async Task Issues_not_authored_by_the_App_never_lock(string login, string type)
    {
        var body = $"<!-- main-watcher lease_until={ValidLease:yyyy-MM-ddTHH:mm:ssZ} -->";
        var github = new FakeGitHub().LockIssues(Issue(3, body, login, type));

        var verdict = await github.Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Pass, verdict.Outcome);
        Assert.Null(verdict.FailOpenReason);
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task A_pull_request_labelled_main_broken_is_not_a_lock()
    {
        var pr = new { number = 4, html_url = "u", body = "", user = new { login = "actium-main-watcher[bot]", type = "Bot" }, pull_request = new { url = "u" } };

        var verdict = await new FakeGitHub().LockIssues(pr).Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Pass, verdict.Outcome);
        Assert.Null(verdict.FailOpenReason);
    }

    [Fact]
    public async Task A_valid_lock_fails_a_group_with_an_unlabelled_pull_request()
    {
        var verdict = await LockedGroup(ValidLease, ["fixes-main"], []).Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Fail, verdict.Outcome);
        Assert.Null(verdict.FailOpenReason);
        Assert.Contains("#7", verdict.Detail);
        Assert.Contains("#2 PR 2", verdict.Detail);
        Assert.DoesNotContain("#1 PR 1", verdict.Detail);
    }

    // TS-S5 in miniature: the fix is the group's head, the unlabelled PR is ahead of it.
    [Fact]
    public async Task A_group_mixing_a_fix_and_a_non_fix_fails_whichever_is_the_head()
    {
        var verdict = await LockedGroup(ValidLease, [], ["fixes-main"]).Gate().DecideAsync(MergeGroup(headPr: 2), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Fail, verdict.Outcome);
        Assert.Contains("#1 PR 1", verdict.Detail);
    }

    // TS-S5 in the sandbox: for a queue entry behind another, base_sha is the head of the entry ahead,
    // so the gate compares from the target branch to see every PR in the batch.
    [Fact]
    public async Task A_batched_entry_is_checked_against_the_target_branch_not_base_sha()
    {
        var github = LockedGroup(ValidLease, [], ["fixes-main"]);
        var entryBehind = new GateEvent("merge_group", Sha('c'), Sha('f'), $"gh-readonly-queue/main/pr-2-{Sha('c')}", "refs/heads/main");

        var verdict = await github.Gate().DecideAsync(entryBehind, Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Fail, verdict.Outcome);
        Assert.Contains("#1 PR 1", verdict.Detail);
        Assert.DoesNotContain(github.Requests, request => request.Contains($"compare/{Sha('c')}"));
    }

    [Fact]
    public async Task A_valid_lock_passes_a_group_where_every_pull_request_is_a_fix()
    {
        var github = LockedGroup(ValidLease, ["fixes-main"], ["bug", "fixes-main"]);

        var verdict = await github.Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Pass, verdict.Outcome);
        Assert.Null(verdict.FailOpenReason);
        // Every commit on head_sha that is not yet on main was checked.
        foreach (var sha in new[] { '1', 'b', '2', 'c' })
            Assert.Contains($"/repos/{Repo}/commits/{Sha(sha)}/pulls?per_page=100", github.Requests);
    }

    [Fact]
    public async Task Pull_requests_named_only_in_commit_subjects_or_the_queue_branch_are_checked()
    {
        // A squash-merged group: the rewritten commits have no associated PRs.
        var github = new FakeGitHub()
            .LockIssues(Lock(7, ValidLease))
            .Compare("main", Sha('f'), Commit(Sha('b'), "Fix the build (#1)\n\nDetails"), Commit(Sha('c'), "Add a feature"))
            .CommitPulls(Sha('b'))
            .CommitPulls(Sha('c'))
            .Respond($"/repos/{Repo}/pulls/1", Pull(1, "fixes-main"))
            .Respond($"/repos/{Repo}/pulls/2", Pull(2));

        var verdict = await github.Gate().DecideAsync(MergeGroup(headPr: 2), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Fail, verdict.Outcome);
        Assert.Contains("#2 PR 2", verdict.Detail);
    }

    [Fact]
    public async Task Associated_pull_requests_that_are_closed_or_target_another_branch_are_ignored()
    {
        var closed = new { number = 8, title = "old", state = "closed", @base = new { @ref = "main" }, labels = Array.Empty<object>() };
        var otherBranch = new { number = 9, title = "stacked", state = "open", @base = new { @ref = "feature" }, labels = Array.Empty<object>() };
        var github = new FakeGitHub()
            .LockIssues(Lock(7, ValidLease))
            .Compare("main", Sha('f'), Commit(Sha('2')))
            .CommitPulls(Sha('2'), Pull(2, "fixes-main"), closed, otherBranch);

        var verdict = await github.Gate().DecideAsync(MergeGroup(headPr: 2), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Pass, verdict.Outcome);
    }

    [Fact]
    public async Task A_valid_lock_fails_a_group_in_which_no_pull_request_can_be_found()
    {
        var github = new FakeGitHub()
            .LockIssues(Lock(7, ValidLease))
            .Compare(Sha('a'), Sha('f'), Commit(Sha('b')))
            .CommitPulls(Sha('b'));

        var verdict = await github.Gate().DecideAsync(new GateEvent("merge_group", Sha('a'), Sha('f'), "not-a-queue-branch"), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Fail, verdict.Outcome);
        Assert.Contains("no pull request", verdict.Detail);
    }

    // TS-U9 at the gate: a missing, unreadable, expired or too-distant lease fails open.
    [Theory]
    [InlineData("no marker")]
    [InlineData("<!-- main-watcher lease_until=soon -->")]
    [InlineData("<!-- main-watcher lease_until=2026-09-16T11:59:59Z -->")]
    [InlineData("<!-- main-watcher lease_until=2026-09-17T12:00:01Z -->")]
    public async Task A_lock_without_a_valid_lease_fails_open_with_LOCK_LEASE_EXPIRED(string body)
    {
        var github = new FakeGitHub().LockIssues(Issue(7, body));

        var verdict = await github.Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Pass, verdict.Outcome);
        Assert.Equal(GateVerdict.LeaseExpired, verdict.FailOpenReason);
        Assert.Equal("LOCK LEASE EXPIRED — failed open", verdict.Title);
        Assert.Contains("#7", verdict.Detail);
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task One_lock_with_a_valid_lease_is_enforced_even_if_another_has_lapsed()
    {
        var github = LockedGroup(ValidLease, [], []).LockIssues(Lock(5, Now.AddHours(-1)), Lock(7, ValidLease));

        var verdict = await github.Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Fail, verdict.Outcome);
        Assert.Contains("#7", verdict.Detail);
    }

    [Fact]
    public async Task API_unreachable_after_3_retries_fails_open_with_LOCK_STATUS_UNKNOWN()
    {
        var github = new FakeGitHub().Respond($"/repos/{Repo}/issues?state=open&labels=main-broken&per_page=100",
            () => throw new HttpRequestException("Connection refused"));

        var verdict = await github.Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Pass, verdict.Outcome);
        Assert.Equal(GateVerdict.ApiError, verdict.FailOpenReason);
        Assert.Equal("LOCK STATUS UNKNOWN — failed open", verdict.Title);
        Assert.Equal(4, github.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], github.Delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Transient_errors_are_retried_and_a_later_success_is_used(HttpStatusCode status)
    {
        var calls = 0;
        var github = new FakeGitHub()
            .Respond($"/repos/{Repo}/issues?state=open&labels=main-broken&per_page=100",
                () => ++calls < 4 ? Json(status, new { message = "try later" }) : Json(HttpStatusCode.OK, Array.Empty<object>()));

        var verdict = await github.Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Pass, verdict.Outcome);
        Assert.Null(verdict.FailOpenReason);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task A_non_transient_API_error_fails_open_without_retrying()
    {
        var github = new FakeGitHub().LockIssues().Respond($"/repos/{Repo}/issues?state=open&labels=main-broken&per_page=100",
            () => Json(HttpStatusCode.NotFound, new { message = "Not Found" }));

        var verdict = await github.Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateVerdict.ApiError, verdict.FailOpenReason);
        Assert.Single(github.Requests);
        Assert.Empty(github.Delays);
    }

    [Fact]
    public async Task An_API_error_while_finding_the_group_pull_requests_fails_open()
    {
        var github = new FakeGitHub()
            .LockIssues(Lock(7, ValidLease))
            .Respond($"/repos/{Repo}/compare/main...{Sha('f')}?per_page=100&page=1",
                () => Json(HttpStatusCode.ServiceUnavailable, new { message = "unavailable" }));

        var verdict = await github.Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Pass, verdict.Outcome);
        Assert.Equal(GateVerdict.ApiError, verdict.FailOpenReason);
    }

    [Fact]
    public async Task Lock_issues_and_compare_results_are_read_across_pages()
    {
        var page2 = $"https://api.github.test/repos/{Repo}/issues?state=open&labels=main-broken&per_page=100&page=2";
        var firstPage = Enumerable.Range(100, 100).Select(n => Issue(n, "hand-made", "maintainer", "User")).ToArray();
        var commits = Enumerable.Range(0, 150).Select(i => Commit(i.ToString("x40"))).ToArray();
        var github = new FakeGitHub()
            .Respond($"/repos/{Repo}/issues?state=open&labels=main-broken&per_page=100", () =>
            {
                var response = Json(HttpStatusCode.OK, firstPage);
                response.Headers.Add("Link", $"<{page2}>; rel=\"next\", <{page2}>; rel=\"last\"");
                return response;
            })
            .Respond($"/repos/{Repo}/issues?state=open&labels=main-broken&per_page=100&page=2", new[] { Lock(7, ValidLease) })
            .Respond($"/repos/{Repo}/compare/main...{Sha('f')}?per_page=100&page=1", new { total_commits = 150, commits = commits[..100] })
            .Respond($"/repos/{Repo}/compare/main...{Sha('f')}?per_page=100&page=2", new { total_commits = 150, commits = commits[100..] });
        foreach (var i in Enumerable.Range(0, 150))
            github.CommitPulls(i.ToString("x40"), Pull(2));

        var verdict = await github.Gate().DecideAsync(MergeGroup(), Now, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Fail, verdict.Outcome);
        Assert.Contains($"/repos/{Repo}/commits/{149.ToString("x40")}/pulls?per_page=100", github.Requests);
    }

    [Fact]
    public void Reads_the_merge_group_fields_from_the_event_payload()
    {
        using var payload = JsonDocument.Parse($$$"""
            {"action":"checks_requested","merge_group":{"base_sha":"{{{Sha('a')}}}","head_sha":"{{{Sha('f')}}}","head_ref":"gh-readonly-queue/main/pr-2-{{{Sha('a')}}}","base_ref":"refs/heads/main"}}
            """);

        Assert.Equal(MergeGroup(), GateEvent.Read("merge_group", payload.RootElement));
        Assert.Equal(new GateEvent("pull_request"), GateEvent.Read("pull_request", JsonDocument.Parse("{}").RootElement));
    }
}
