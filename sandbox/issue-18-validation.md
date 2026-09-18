---
owner: platform-team
reviewed: 2026-09-18
review_by: 2027-03-18
---

# Issue #18 sandbox validation

Validated on 2026-09-18 with the stale-run work pushed to
`main-watcher-sandbox/main-watcher` (the private watcher replica) as
[`34f6e26`](https://github.com/main-watcher-sandbox/main-watcher/commit/34f6e26117a0cc7a127c5bcadbc1a56602f770f4)
at 03:09Z, whose tree is MainWatcher `87d239c`. The target is
`main-watcher-sandbox/sample-target` with `poll_interval: 1`. The trigger worker ran in the
`main-watcher-sandbox` namespace throughout, on the same image, so the worker's stale-run
flagging and the Planner's stale-run lifecycle were exercised together on one rule.

The replica variable `MW_QUEUE_DEADLINE_MINUTES` and the worker's ConfigMap entry of the same
name were both set to **5**, so the 30-minute queue deadline of ADR-013 is 5 minutes below.
Every check-run listing uses `?filter=all`.



## Substitutions from TS-001

TS-S16 (g) is written around "a single busy sandbox runner released after 8 min". No
self-hosted runner was available, so the same property was arranged with a 5-minute queue
deadline instead of 10: the job started 7 seconds after its check run, well inside the deadline,
and then ran for 8m25s, so the check run was 1.7 × the queue deadline old when the job finished
and nothing cancelled it. The rule under test is the same one either way — the queue deadline
stops applying the moment the job starts, and the run deadline takes over.

TS-S16 (h) needs a job that outlives its own deadline. GitHub's job timeout is the target's
`timeout` plus 20 minutes and normally ends a job first, which is exactly what ADR-013 says; a
job that outlives it is the lost-runner case. That was arranged by lowering the target's
`timeout` in the replica's `targets.yml` **after** the job had started: the running job keeps the
`timeout-minutes` it was created with, while the watcher judged it against the smaller number.

**That technique was itself the bug**, as the review of PR #49 found, and it no longer works. See
[After the review](#after-the-review-the-deadline-no-longer-follows-the-configuration) below for
what it does and does not leave standing.

## TS-S16 (g): the two deadlines, and no second test until a cancelled run has stopped

### A job that starts inside the queue deadline is never cancelled for the check run's age

Head [`92ac3d3`](https://github.com/main-watcher-sandbox/sample-target/commit/92ac3d3b13f5e21b0340728801bdd9fd6333b8b8),
pushed 03:13:05Z with `failing_tests: ["Alpha"]` and `slow_suite_minutes: 8`.

| Time (UTC) | Event |
| --- | --- |
| 03:13:18 | Worker: `started watch.yml because head 92ac3d3 is eligible for a test` |
| 03:14:08 | Check run 105467680139 created; target run [35302438201](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35302438201) |
| 03:14:15 | The `main-watcher-tests / main-watcher` job starts — 7 s after the check run, inside the 5-minute deadline |
| 03:19:08 | The check run is 5 minutes old: the queue deadline **would** have fallen here had the job not started |
| 03:14–03:22 | Nine worker cycles: `1 targets, 0 dispatched, 0 errors` every minute. Nothing was flagged stale |
| 03:22:40 | The job completes `failure`, 8m25s after it started and 8m32s after the check run was created — 1.7 × the queue deadline |
| 03:23:17 | Worker: `started watch.yml because check 105467680139: the main-watcher job of target run 35302438201 has completed` |
| 03:24:19 | Check 105467680139 completes `failure`, "Tests failed"; lock [sample-target#30](https://github.com/main-watcher-sandbox/sample-target/issues/30) opened |

The check run's output carries no `cancel_requested` marker, and the run deadline for this job
was its start plus the target's 30-minute `timeout`, 20 minutes and 10 minutes — 03:14:15 + 60
minutes — which it never reached.

### A job that never gets a runner is cancelled at the queue deadline and judged once stopped

The caller was given `runs-on: sandbox-no-such-runner`, which no runner has, in head
[`11f689a`](https://github.com/main-watcher-sandbox/sample-target/commit/11f689a50b5d91bc2c4ef98edeb47e2f97690b06)
at 03:25:14Z.

| Time (UTC) | Event |
| --- | --- |
| 03:25:17 | Worker: `started watch.yml because head 11f689a is eligible for a test` |
| 03:26:11 | Check run 105469966647 created; target run [35303199218](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35303199218) |
| 03:26:16 | The job is created with status `queued` — **and `started_at: 03:26:16Z`** (see below) |
| 03:27:29 | Head [`90ded26`](https://github.com/main-watcher-sandbox/sample-target/commit/90ded2600c93af622e89d1e83a4eb24596fd0f78) pushed, removing `runs-on`: a newer head, while the stale run is still pending |
| 03:28–03:30 | Worker cycles dispatch nothing: the check run is in progress, so the newer head is not eligible |
| 03:31:17 | Worker: `started watch.yml because check 105469966647: target run 35303199218 did not start within 5 minutes` |
| 03:32:11 | The cycle writes the check run's output — title **Stopping a stale target run**, marker `cancel_requested=2026-09-18T03:32:11Z` — and cancels the run. The check run stays `in_progress` |
| 03:32:12 | The `main-watcher` job completes `cancelled`, with no steps |
| 03:33:17 | Worker: `started watch.yml because check 105469966647: the main-watcher job of target run 35303199218 has completed` |
| 03:34:11 | Check 105469966647 completes **`neutral`**, title "Infrastructure error": *"the tests did not finish … Job main-watcher-tests / main-watcher has no steps … Main Watcher cancelled this run at 2026-09-18T03:32:11Z after it passed its deadline."* No lock was touched; #30 stayed open and gained no comment |
| 03:34:12 | Check run 105471441264 starts on the newer head `90ded26` — one second after the stale check completed |

**`started_at` on a queued job.** GitHub gave the job that never got a runner
`started_at: 2026-09-18T03:26:16Z`, two seconds after the run was created and six minutes before
anything ran. A run deadline derived from that time would have been 03:26:16 + 60 minutes and the
run would never have been cancelled. `StaleRun.Started` reads the job's **status** for exactly
this reason, and this run is the measurement behind that choice rather than an assumption.

## TS-S16 (h): the run deadline, and a run that will not stop

Both halves use `hang_upload_forever`, a new sandbox switch: a step after
`main-watcher-tests-finished` with no `timeout-minutes` of its own that never ends. The tests
fail and the marker step succeeds, and then the job runs on.

**Why the target's `timeout` is changed mid-flight.** The job's `timeout-minutes` is the target's
`timeout` plus 20, fixed when the run is created; the run deadline is the job's `started_at` plus
the target's `timeout` as `targets.yml` reads *now*, plus 20, plus 10. With one value for both,
GitHub's job timeout always falls 10 minutes before the run deadline — which is what ADR-013
says it should, the deadline being a backstop for a job GitHub has *not* ended. Lowering
`targets.yml` after the job starts makes the watcher judge a long-lived job against a short
timeout, which is the same asymmetry a lost runner produces.

### A job that will not finish is cancelled at its run deadline, and the lock still opens

Head [`85b6636`](https://github.com/main-watcher-sandbox/sample-target/commit/85b663655c099588ccced7ec7d331c078fe87310),
pushed 03:46:46Z with `failing_tests: ["Alpha"]` and `hang_upload_forever: true`, while
`targets.yml` and the caller said `timeout: 30` (job `timeout-minutes: 50`).

| Time (UTC) | Event |
| --- | --- |
| 03:48:09 | Check run 105473991720 created; target run [35304569788](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35304569788) |
| 03:48:17 | The `main-watcher` job starts |
| 03:48:34–03:48:40 | `main-watcher-test: failure`, then `main-watcher-tests-finished: success` — the tests ran to completion and failed |
| 03:48:40 | `sandbox upload hang` starts and never ends. `Upload CTRF reports` is never reached |
| 03:49:13 | `targets.yml` and the caller lowered to `timeout: 2`, so the run deadline becomes 03:48:17 + 2 + 20 + 10 = **04:20:17** (GitHub's own job timeout would have been 04:38:17) |
| 03:49–04:20 | 31 worker cycles, `0 dispatched`: the job is inside its deadline, so the run is not stale and the report is not owed |
| 04:21:17 | Worker: `started watch.yml because check 105473991720: target run 35304569788 has run past its deadline` |
| 04:22:20 | The cycle records `cancel_requested=2026-09-18T04:22:20Z` in the check run's output and cancels the run. The check run stays `in_progress` |
| 04:27:21 | The job completes `cancelled`, five minutes later: GitHub takes that long to tear down a `sleep infinity` step |
| 04:28:13 | Check 105473991720 completes **`failure`**, "Tests failed"; lock [sample-target#31](https://github.com/main-watcher-sandbox/sample-target/issues/31) opened |

The steps as the Reporter read them: `main-watcher-test: failure`,
`main-watcher-tests-finished: success`, `sandbox upload hang: failure`,
`Upload CTRF reports: skipped`. The marker decided the result and the cancellation after it
changed nothing, which is ADR-013's rule that a finished test step wins over anything later in
the run. The artifact was never uploaded, so the lock says "failing tests unknown" (ADR-007), and
the check run's output records the cancellation:
*"Main Watcher cancelled this run at 2026-09-18T04:22:20Z after it passed its deadline."*

### A lost runner, recorded because it is the case the run deadline exists for

The first attempt at the second half did not reach its deadline. Its job hung in a
`sleep infinity` step with no output, and 26 minutes in GitHub reclaimed the runner:
`##[error]The runner has received a shutdown signal.` The job completed `cancelled` at
05:01:44, its `main-watcher-test: failure` and `main-watcher-tests-finished: success` steps
intact, and the Reporter opened the lock from them — a lost runner *after* a finished test is
red, not neutral, which is the ADR-013 table working as written. The sandbox switch now prints a
heartbeat every 20 seconds so a scenario can hold a job open for an hour.

### A run that will not stop blocks the target until it is released

Head [`1a2acd5`](https://github.com/main-watcher-sandbox/sample-target/commit/1a2acd5d5605680db806c0077cd1f99473a3127a),
pushed 05:10:12Z with `failing_tests: ["Alpha"]` and `hang_upload_forever: true`, while
`targets.yml` and the caller said `timeout: 340` (job `timeout-minutes: 360`).
`MW_SANDBOX_REFUSE_CANCEL` was `true`, so every cancel failed without asking GitHub.

| Time (UTC) | Event |
| --- | --- |
| 05:11:16 | Check run 105489345609 created; target run [35309823928](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35309823928); job starts 05:11:24 |
| 05:11:54 | `main-watcher-test: failure`, `main-watcher-tests-finished: success`, then the hang step |
| 05:12:20 | `targets.yml` and the caller lowered to `timeout: 2` (run deadline **05:43:24**), and head [`162ef963`](https://github.com/main-watcher-sandbox/sample-target/commit/162ef963e4fa61010b5d0e623a15e13984d70446) pushed with a green `sandbox.json`: the newer head that must not be tested |
| 05:44:16 | Worker: `started watch.yml because check 105489345609: target run 35309823928 has run past its deadline` |
| 05:45:11 | `cancel_requested=2026-09-18T05:45:11Z` recorded; `cancel refused: the sandbox cancel switch refused it` |
| 05:46–06:00 | A cycle a minute, each logging `was cancelled at 2026-09-18T05:45:11Z and has not stopped; cancel refused: …`. The wait runs from the recorded time, not from each cycle |
| 06:01:16 | 15 minutes on: `force_cancel_requested=2026-09-18T06:01:16Z` recorded **beside** the unchanged `cancel_requested`, and the force-cancel refused |
| 06:02:19 | A **forced dispatch** by hand, `force: true`. Its cycle logged the force-cancel and then `No eligible head.` — `force` lifts only ADR-017's neutral cap, never an active check run |
| 06:16:21 | 15 minutes after the force-cancel: alert [main-watcher#19](https://github.com/main-watcher-sandbox/main-watcher/issues/19), "Target run could not be stopped on `main-watcher-sandbox/sample-target`", carrying `<!-- main-watcher unstoppable check=105489345609 -->` |
| 06:17–06:22 | Cycles keep force-cancelling and the alert gains **no** comments: the marker keys it to the check run |
| — | Throughout, 78 minutes, the newer head `162ef963` had **no `main-watcher` check run and no target run**, and the check run stayed `in_progress` |
| 06:23:05 | The run stopped by hand and deleted at 06:28:15, with the worker scaled to zero so the deletion was not raced by a cycle |
| 06:28:49 | Worker: `started watch.yml because check 105489345609: target run 35309823928 was deleted` |
| 06:30:01 | Check 105489345609 completes **`neutral`**, title "Outcome unknown": *"the target run was deleted … Main Watcher cancelled this run at 2026-09-18T05:45:11Z after it passed its deadline."* Alert [main-watcher#20](https://github.com/main-watcher-sandbox/main-watcher/issues/20) |
| 06:30:02 | Check run 105505060653 starts on `162ef963` — one second later. The target is released |

**GitHub will not delete a running run.** ADR-013 point 5 says "a person can delete the run
instead. That gives 'outcome unknown' (404) and releases the target." `DELETE
/repos/…/actions/runs/35309823928` against the still-running run answered
`403 Could not delete the workflow run`; the delete succeeded only after the run had stopped. So
the operator's escape hatch is two steps, not one: **cancel the run by hand, then delete it** (or
simply let the cancel stop it, which is enough on its own). The wording in ADR-013 is wrong on
this point and needs an amending ADR; `docs/watcher.md` already describes the two-step action.

## Outcome

| Acceptance criterion | Evidence |
| --- | --- |
| The queue deadline counts from check-run creation until the job starts | The 5-minute deadline did not fire for a job that started at +7 s and ran to +8m32s; it did fire for a job that never started |
| The run deadline counts from the job's `started_at` | Both halves were cancelled at the job's start + `timeout` + 20 + 10, not at any time derived from the check run |
| `cancel_requested` is recorded and the run cancelled, force-cancelled 15 min later, the check run kept `in_progress` | Markers at 05:45:11 and 06:01:16, the check run `in_progress` from 05:11:16 to 06:30:01 |
| Once stopped, the outcome table is applied to its steps | Half A: a marker `success` and a failed test step opened a lock after the cancel |
| A 404 gives "outcome unknown" | The deleted run gave `neutral`, "Outcome unknown" |
| A jobs API error changes nothing | Not reachable in the sandbox; covered by TS-U15 |
| The run deadline is counted from the job's own timeout, not the configuration | Measured after the review: a job dispatched at `timeout: 340` was untouched past the deadline the lowered configuration implied (see [After the review](#after-the-review-the-deadline-no-longer-follows-the-configuration)) |
| A crash between steps resumes from the recorded times | The refused cancels are that case: the marker was written before a request that never reached GitHub, and every later cycle continued from the recorded time. Also covered by TS-U15 |
| 15 min after an unsuccessful force-cancel, "target run could not be stopped" is raised and no test starts for that target | Alert main-watcher#19 at 06:16:21; no test for a newer head for 78 minutes, including under a forced dispatch |
| TS-U5 (c): the worker flags runs past either deadline whatever the marker step shows, until the run has stopped | The worker's own log lines drove every cycle above, including for a job whose `main-watcher-tests-finished` step had already succeeded |

## Sandbox restored

`MW_QUEUE_DEADLINE_MINUTES` and `MW_SANDBOX_REFUSE_CANCEL` were deleted from the replica, the
worker's ConfigMap entry was removed and the deployment re-applied, and the target's `timeout` is
back to 30 in both `targets.yml` and the caller. The scenario's `watcher-infra` alerts
(main-watcher #18, #19, #20) are left open as evidence, as is lock sample-target#32 from the
post-review run, which the next green head closes.

## After the review: the deadline no longer follows the configuration

The review of [PR #49](https://github.com/Actium-Group-Corporation/MainWatcher/pull/49) found
that the run deadline was counted from the target's `timeout` **as `targets.yml` reads now**,
while ADR-013 counts it from the job's **own** `timeout-minutes`, fixed when GitHub creates the
run. Lowering a target's `timeout` from 120 to 30 would therefore have cancelled a healthy job at
60 minutes instead of its real 150, and raising it would have delayed detection. The two halves
of TS-S16 (h) above reached the run deadline by exactly that route, which is how the bug came to
be exercised without being noticed.

The Planner now records the dispatched `timeout` in the check run's own output, and
`StaleRun.TestTimeout` reads it back; `TheRunDeadlineUsesTheTimeoutTheRunWasDispatchedWith`
covers both directions and fails against the old code.

### The regression, measured

A run under the fixed code, with the old technique applied to it.

| Time (UTC) | Event |
| --- | --- |
| 11:37:30 | Head [`f41dcad`](https://github.com/main-watcher-sandbox/sample-target/commit/f41dcadf430516a9e16109e69872f0ff28350a29) pushed with `failing_tests: ["Alpha"]` and `hang_upload_forever: true`, while `targets.yml` and the caller said `timeout: 340` |
| 11:38:39 | Check run 105585520362 created; target run [35340649104](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35340649104). Its output carries `<!-- main-watcher timeout_minutes=340 -->`, written by the same call that created it |
| 11:38:50 | The job starts; by 11:39 `main-watcher-test: failure` and `main-watcher-tests-finished: success`, then the hang step |
| 11:39:48 | `targets.yml` and the caller lowered to `timeout: 2` — **the old technique**. Under the old rule the deadline would now be 11:38:50 + 2 + 20 + 10 = **12:10:50** |
| 12:10:50 | Nothing happens |
| 12:12:15 | The check run is still `in_progress`, still titled "Tests running", still recording `timeout_minutes=340`, with no `cancel_requested`. The run is still `in_progress`. The worker has dispatched **no** cycle since 11:37:46: it never flagged the run, because it reads the same recorded value |

Under the old code this healthy job would have been cancelled at 12:10:50 and its check run taken
out of service. The recorded deadline is 11:38:50 + 340 + 30 = 18:08:50, which is what a job
dispatched with `timeout: 340` is entitled to.

The run was then cancelled by hand at 12:13:15 to release the target. Its steps still read
`main-watcher-test: failure` and `main-watcher-tests-finished: success`, so the Reporter completed
check 105585520362 as **`failure`** at 12:19:32 and opened lock
[sample-target#32](https://github.com/main-watcher-sandbox/sample-target/issues/32) — the ADR-013
table again preferring a finished test step over the cancellation that followed it.

### What the earlier TS-S16 (h) evidence still shows

- **Unaffected.** Everything from `cancel_requested` onwards: the cancel and its refusals, the
  force-cancel 15 minutes later, the "target run could not be stopped" alert 15 minutes after
  that, no test for a newer head across 78 minutes including under a forced dispatch, release by
  deleting the run, and the outcome table judging a stopped job. None of it depends on how the
  deadline was computed, only on its having fired.
- **Superseded.** How the deadline was *reached*. Lowering `targets.yml` mid-flight was the bug,
  not a test fixture, and the fix removes it. Reaching a run deadline in the sandbox now needs a
  job whose real `timeout-minutes` exceeds the recorded one — a gate branch with a literal long
  job timeout would do it — and that was not re-run, because the deadline arithmetic is covered
  by TS-U15 and the regression above measures the property that changed.
- **Unaffected.** All of TS-S16 (g): the queue deadline is counted from the check run's creation
  either way, and no part of it reads the target's `timeout`.
