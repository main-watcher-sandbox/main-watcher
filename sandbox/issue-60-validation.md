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
- 6 lock-issue reads, for recovery, renewal, the queue sweep, planning and the override check;
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
  until it finds a green one, and never more than 50 commits (`GitHubGateway.ChecksLimit`). This is exact for pending
  runs, the newest runs and the neutral-retry count: only the Planner creates check runs, on the head, and never while
  one is pending (ADR-017). So a pending run is always the newest run, and runs on older commits are older. What the limit
  can miss:
  - a green run more than 50 commits back, which only the timing comparison reads (the push list walks back on its own);
  - a pending run with more than 50 pushes after it.

  Estimated from the logs above:
  - a dispatch cycle drops from 208 to about 17 requests: 2 check-runs reads to reach the previous green commit, 1 more
    for planning, and one page of commits instead of two;
  - a report cycle drops to about 21.

  The next suite run is to confirm both figures.
- **A low budget alerts.** After every cycle, including one that failed, `InstallationBudget.Judge` raises "The
  main-watcher App's API budget is below 20%" when the lowest budget the cycle saw had less than 20% left.
- **A refused cycle alerts.** The gateway records the first request GitHub refused on its rate limit:
  - a 429;
  - a 403 with `x-ratelimit-remaining: 0`, the primary limit;
  - a 403 with `retry-after`, the secondary limit.

  The cycle then raises "The main-watcher App's API rate limit is refusing cycles", quoting the refusal. The record is
  made wherever the refusal was caught, since the cycle can fail on it at any step.
- **One alert per window.** Both alerts carry a key naming the minute the budget refills. However many cycles and targets
  see one shortage, it is one issue, with at most one comment an hour.
- **The alerts can always be written.** They use the workflow's `GITHUB_TOKEN`, whose budget is separate from the App's.
- **ETags were considered and deferred.** After the fix, a cycle's reads are about 20, and most of them are expected to
  change between cycles: the head, the issues, the jobs.

Unit tests:
- `GatewayTests.AFirstReadOfTheChecksStopsAtTheLastGreenRun`
- `GatewayTests.TheBudgetIsTheLowestRateLimitOfItsWindow`
- `GatewayTests.ARateLimitRefusalIsRecordedWhereverTheCycleCaughtIt`
- `WatcherTests.ALowInstallationBudgetRaisesOneAlertPerWindow`
- `WatcherTests.ACycleRefusedByTheRateLimitRaisesAnAlertNamingIt`

## Still to do in the sandbox

This change has not yet run in the sandbox. A scenario suite run was using the replica while it was written, so it was
not deployed there. After merging, the next suite run should show two things:
- the per-cycle request counts falling to about 20;
- no cycle refused on the budget.

At about 20 requests a cycle, the busy phase of runs 6 to 8 (56 cycles in 23 minutes, about 150 an hour) would cost about
3,000 requests an hour, inside the 5000.
