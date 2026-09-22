---
owner: platform-team
reviewed: 2026-09-22
review_by: 2027-03-15
---

# Issue #60 validation — the `main-watcher` installation's API budget

[MainWatcher#60](https://github.com/Actium-Group-Corporation/MainWatcher/issues/60) asked for four things:

- measure what each kind of `watch.yml` cycle costs the `main-watcher` App installation;
- raise an alert when that budget runs low;
- raise an alert when a cycle is refused by the rate limit;
- rewrite R-13.

Every target's cycles share this installation's 5000 requests an hour. The scenario suite spent it with six to ten busy
targets (#25, `issue-25-validation.md`).

All times UTC.

## What a cycle cost

Since `0662df0`, each cycle ends by logging its requests by endpoint, with numbers and SHAs generalised. The eight cycles
below ran on 2026-09-22 between 13:35 and 13:58, in the replica `main-watcher-sandbox/main-watcher`, for
`main-watcher-sandbox/sample-target`. That target then had 193 to 197 commits on `main`. They alternate between two cycle
kinds: a test dispatch for a new head, then the report of that test.

| Run | Kind | Requests | Of which check-runs reads, one per commit | Budget logged |
|---|---|---|---|---|
| 35734429826 | Dispatch | 208 | 193 | 4988 |
| 35734769118 | Report | 212 | 194 | 4609 |
| 35735308450 | Dispatch | 209 | 194 | 4954 |
| 35735674366 | Report | 213 | 195 | 4936 |
| 35735902360 | Dispatch | 210 | 195 | 4916 |
| 35736238158 | Report | 214 | 196 | 4893 |
| 35736466253 | Dispatch | 211 | 196 | 4873 |
| 35736804335 | Report | 215 | 197 | 4855 |

The rest of a dispatch cycle:
- 6 reads of the lock issues;
- 3 reads of the head;
- 2 pages of commits;
- the caller file;
- the check run's creation, the dispatch and the link.

The rest of a report cycle:
- 8 issue reads and 4 head reads;
- the jobs, the artifact list and the artifact;
- the check run's update and completion.

**The dominant cost was one check-runs read per commit on `main`,** 93% of every cycle. `GitHubGateway.Checks` read the
check runs of every commit the first time it was called. That call was meant to happen once in a worker's lifetime, but
the watcher is a new process each cycle, so every cycle made it. The cost grew by one request with every push. On a target
with a few thousand commits, one cycle would have spent the hour's budget alone.

#25's runs 6 to 8 had estimated about 27 requests for an idle cycle and over 70 in the busy phase. Those figures came from
how far the logged budget fell, which undercounts, as the next paragraph shows.

**Cycle kinds not separately measured.** Reconciliation, a sweep leg and a stale-run cycle all made the same first
`Checks` call. They differ from the two kinds above only by a few requests of their own:
- reconciliation adds one activity read per target, plus a compare and the label events for each merge it judges;
- a sweep leg adds a `watch.yml` run list and gate-run reads;
- a stale-run cycle adds a run read and a cancel.

The next suite run will log each of them.

**The budget line is not the whole budget.** The logged budget fell by about 20 a cycle, while each cycle counted about
210 requests. The report cycle at 13:39 logged 4609 instead, from a budget refilling at 14:35:43, not 14:35:41. That
budget fell about 380 over two cycles. This fits the check-runs reads being charged to a second budget. #25's runs saw
the same thing: two legs of one sweep logged 0 left while four logged about 2800. Which requests GitHub charges to which
budget cannot be seen from outside. The gateway therefore now keeps the **lowest** budget it saw in the window, not the
latest one.

## What changed

- **A first `Checks` read stops at the last green run.** It walks back from the head and reads each commit's check runs
  until it finds a green one, or until it has read 50 commits (`GitHubGateway.ChecksLimit`). Only the Planner creates check
  runs, on the head, and never while one is pending (ADR-017). So a pending run is always the newest run, and runs on older
  commits are older. Finding the newest check run finds every pending one, however many pushes followed it. What the limit
  leaves out is a green run further back, which only the timing comparison reads (the push list walks back on its own).

  **When those 50 commits hold no check run at all, the rest of the history is searched with no limit**
  (`NewestCheckedCommit`), through GraphQL, 100 commits a request. The PR #62 review rejected two earlier versions: the
  first stopped at 50 commits whatever it had found, and the second at 1000, each of which could hide the pending run this
  has to find. GraphQL has a budget of its own, separate from the REST requests the cycles spend, and a page of 100 commits
  costs 2 of its 5000 points an hour: measured against `sample-target`, where the query also returned the commits in the
  same order as the REST list. So even a first cycle on a repository with a long history and no check run costs the
  installation's hourly requests nothing.

  Estimated from the logs above:
  - a dispatch cycle drops from 208 to about 17 requests: 2 check-runs reads to reach the previous green commit, 1 more
    for planning, and one page of commits instead of two;
  - a report cycle drops to about 21.

  The full suite run below confirmed both.
- **A low budget alerts.** After every cycle, including one that failed, `InstallationBudget.Judge` raises "The
  main-watcher App's API budget is below 20%" when the lowest budget the cycle saw had less than 20% left.
- **A refused cycle alerts.** The gateway records the first request GitHub refused on its rate limit:
  - a 429;
  - a 403 with `x-ratelimit-remaining: 0`, the primary limit;
  - a 403 with `retry-after`, or with a message naming a rate limit, the secondary limit. GitHub documents `retry-after`
    as optional there, and a secondary limit leaves the primary budget untouched (PR #62 review).

  The cycle then raises "The main-watcher App's API rate limit is refusing cycles", quoting the refusal. The record is
  made wherever the refusal was caught, since the cycle can fail on it at any step.
- **One alert per window, written once.** Both alerts carry a key naming the minute the budget refills. Cycles for
  different targets, and a sweep's legs, run at the same moment, and each checks before it writes, so each can find nothing
  written. Both alerts are therefore raised as `shared`, which claims the window before writing it: the cycle creates the
  label `mw-claim-<digest>` in the watcher repository, the digest being of the alert's title and its window key, and only
  the cycle GitHub lets create it writes. A label name is unique in a repository, so exactly one cycle wins, and no
  duplicate issue or comment is created — and so none is notified. The PR #62 review rejected two earlier versions: the
  first wrote and then tidied up, by which time the duplicate notifications had gone out; the second named the claim after
  the claiming cycle's own minute as well, so two cycles either side of a minute boundary claimed different labels for one
  window and both wrote. The name now depends on nothing but the alert and its window. A claim whose write fails is deleted
  again, so the next cycle raises the alert; the description records when it was claimed, and claims older than a day are
  deleted by the next winner. Creating and deleting labels needs no permission beyond the `issues: write` the alerts
  already use.
- **The alerts can always be written.** They use the workflow's `GITHUB_TOKEN`, whose budget is separate from the App's.
- **ETags were considered and deferred.** After the fix, a cycle's reads are about 20, and most of them are expected to
  change between cycles: the head, the issues, the jobs.

Unit tests:
- `GatewayTests.AFirstReadOfTheChecksStopsAtTheLastGreenRun`
- `GatewayTests.TheBudgetIsTheLowestRateLimitOfItsWindow`
- `GatewayTests.ARateLimitRefusalIsRecordedWhereverTheCycleCaughtIt`
- `WatcherTests.ALowInstallationBudgetRaisesOneAlertPerWindow`
- `WatcherTests.ACycleRefusedByTheRateLimitRaisesAnAlertNamingIt`

## The first full suite run with the fix

Run 9 of the scenario suite was on 2026-09-22, 14:35 to 16:32, at `22ffc39`, on six targets.

- **Result:** 23 of 25 units passed in 116 minutes.
- **The two failures were in the suite, not Main Watcher.** Both are fixed in `0636e51`:
  - **TS-S15:** it read #30 in `sample-target-3` as open, unmerged and out of the queue, in the second when the queue
    removed and merged it (both 14:57:42Z). It failed on "left the merge queue without merging", though #30 had merged.
    A queue state that says so is now read again after 10 s, in both queue waits.
  - **TS-S8:** three checks failed:
    - `mw-observer`'s refused issue write on the replica was an empty new issue. The replica has been public since #25,
      and there GitHub answers that with 422, from validation, before its 403. It is now probed with an empty update to
      an existing issue, as on the targets.
    - The two installation checks expected the Apps on the six targets the run used. Both Apps are installed on all ten
      pool targets. The suite now passes the whole pool as `MW_INSTALLED_TARGETS`.
- **The budget held.** 209 cycles sent 4603 requests, about 2,400 an hour:
  - no request was refused;
  - no budget alert was raised;
  - the lowest budget any cycle logged was 3016 of 5000.

  Runs 6 to 8 spent the budget in this phase.

Requests per cycle, by what the cycle did:

| Cycle | Cycles | Median | Range |
|---|---|---|---|
| Test dispatch | 47 | 17 | 17–21 |
| Test dispatch, with reconciliation or an override | 27 | 22 | 20–42 |
| Report | 35 | 21 | 16–36 |
| Report, with reconciliation or an override | 33 | 31 | 21–35 |
| Stale run (cancel, force-cancel, waiting for either) | 33 | 17 | 16–22 |
| Idle: no eligible head | 11 | 15 | 14–19 |
| Sweep leg | 6 | 30 | 22–40 |
| Other, or a pending check with nothing to report | 17 | 21 | 20–21 |

The dispatch and report figures are the 17 and 21 estimated above. Check-runs reads fell from about 195 a cycle to 3 to 5.

Across all 209 cycles, the endpoints that dominate now are all per-cycle reads, none of which grows with history:
- 1379 issue reads, the largest single cost, about 7 a cycle;
- 974 check-runs reads;
- 695 reads of the head.

Most of a cycle's issue reads list the same lock issues again: renewal, the queue sweep, the report, the override check and
reconciliation each read them for themselves. Reading them once a cycle is the next saving, if the budget needs one.

