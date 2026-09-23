---
id: ADR-013
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "the 'reporting pending' alert fires more than once a month, duplicate lock issues or comments are seen, or a real failure is reported as neutral"
sources: [FR-3, FR-4, ADR-003, ADR-004, ADR-007, ADR-009, ADR-010, ADR-015]
confidence: confirmed
amends: [ADR-003, ADR-010]
---

# ADR-013 — The Reporter completes the check run last, so an interrupted report is replayed (amends ADR-003 and ADR-010)

> **Amended by [ADR-019](ADR-019-wait-for-final-job-steps.md) on 2026-09-23.** A completed job
> can still have steps GitHub has not written down. Where the table would give "the tests did not
> finish", a job whose steps are not final (a step without a conclusion, or no `Complete job` step
> last) is not judged for up to 5 minutes after it completed. Until then, the check run stays
> `in_progress` and the worker flags no report. After that, the job is judged as it stands.

> **Amended by [ADR-020](ADR-020-stuck-watch-runs.md) on 2026-09-23.** Point 5's stale-run
> lifecycle also applies to the watcher's own runs. A `watch.yml` run for a target that has not
> started 20 minutes after it was created (plus any wait timer) is cancelled by the worker, then
> force-cancelled, then alerted about, unless it is waiting for a listed reviewer. Sweep runs are
> left alone.

**Deciders:** requester (confirmed 2026-09-15, CQ-10), platform team · **Consulted:** —

## Context

An adversarial review of the architecture on 2026-09-15 found a gap in the reporting flow
(ARCH-001 §5.1).

The Reporter completed the check run **before** it created or updated the lock issue. If
the Reporter crashed, or ran out of API retries, between those two writes:
- the failing commit had a completed check run but no lock;
- the worker no longer flagged work, because it only follows in-progress check runs
  (ADR-010);
- the head was never retested, because it already had a check run (ADR-003).

Nothing durable recorded that the issue write was still owed, so a red `main` could stay
unlocked indefinitely. That breaks FR-4 and the top quality attribute, correctness of the
lock.

A second review of this decision, on the same day, found two further ways recovery could
go wrong:
- **A missing artifact could hide a real failure.** The stale-run rule marked a completed
  run without a CTRF artifact as `neutral`. ADR-007 says a failing test run without valid
  CTRF is still red, with "failing tests unknown". So a failed upload could suppress a
  lock.
- **Replay could undo an override.** If the Reporter created a lock and crashed, and a
  human closed that lock before the replay, a replay that looked only at open issues would
  create a new lock for the same result. ADR-004 says an override lasts until a failing
  run on a newer commit.

There is no datastore to hold a "pending" flag or a test outcome (ADR-003), so the fix has
to use state that GitHub already holds.

## Decision

**1. The test outcome does not depend on the artifact.** The reusable workflow
(ADR-009):
- runs checkout, toolchain setup and `dotnet restore` as **setup steps**;
- runs build and tests, including the one retry of failed tests (CQ-4), in a single step
  named `main-watcher-test`, inside a job named `main-watcher`. The step runs the target's
  command through a small wrapper that enforces the target's `timeout` itself:
  - when the command exits on its own, with any exit code, the wrapper sets the step output
    `finished=true` and exits with the command's exit code, which ADR-007 relies on;
  - when the deadline is reached, it stops the command's whole process tree and exits
    non-zero **without** setting `finished`;
  - the step has no `timeout-minutes` and no `continue-on-error`;
- runs a marker step named `main-watcher-tests-finished` immediately afterwards, with the
  condition `always() && steps.<test step id>.outputs.finished == 'true'`. It does nothing
  but succeed, so its `success` is GitHub's own record that the tests ran to completion;
- writes `timings.json` and uploads the CTRF artifact in later steps that run even when
  `main-watcher-test` fails. Those steps have their own short `timeout-minutes`;
- sets the job's `timeout-minutes` to the target's `timeout` plus a margin for setup and
  upload, so the wrapper's deadline is reached before the job's.

Restore counts as setup and build counts as test: a package-feed outage
should not lock the queue, but a compile break on `main` should.

**Finding the steps.** The Actions jobs API returns each step's name, number and
conclusion, but not the step ID from the workflow file, as a third review on 2026-09-15
pointed out. So the contract is the step **names**:
- the Reporter reads the jobs of the run's latest attempt, and looks for the steps named
  `main-watcher-test` and `main-watcher-tests-finished` in a job whose name ends in
  `main-watcher`. A job from a called workflow is listed with the caller's job name in
  front `[assumption]`;
- both names are literals, never expressions, and no other step in the reusable workflow
  uses them;
- a name found more than once is a **contract error**: `neutral`, with a `watcher-infra`
  alert "outcome contract broken". A missing marker step is no evidence that the tests
  finished, so it is also `neutral` (see the table). Neither case is ever red or green, or
  leaves a report pending forever;
- matching is verified against a real jobs response from a reusable-workflow caller, in the
  sandbox (TS-S16) and in the onboarding dry run.

GitHub records step conclusions whether or not any upload worked.

**Only a finished test run decides.** Two reviews on 2026-09-15 shaped this rule:
- the fifth found that treating every cancelled or timed-out run as neutral let a hung
  upload hide a real failure, so a finished test step must win over anything later in the
  run;
- the sixth found that GitHub reports a step that hits its own `timeout-minutes` as
  `failure`, so a step's conclusion alone cannot tell a failing test from a timeout.

The marker step settles both. The test step's red or green counts only when
`main-watcher-tests-finished` succeeded. Anything that interrupts the tests leaves no
marker and gives `neutral`: the wrapper's deadline, a step or job timeout, a cancellation,
or a lost runner. Nothing after the marker, such as a hung upload, can change the result.

Rows are checked from top to bottom:

| Condition | Result |
|---|---|
| The run was deleted after completing (the API returns 404) | `neutral`, alert "outcome unknown" |
| `main-watcher-test` or `main-watcher-tests-finished` found more than once | Contract error: `neutral`, alert "outcome contract broken" |
| `main-watcher-tests-finished` missing, or not `success` | The tests did not finish (setup failure, deadline, timeout, cancellation or lost runner): infrastructure error, `neutral`, with an alert listing the step conclusions found |
| Marker `success`; `main-watcher-test` `failure`; CTRF valid | Red; failing tests listed |
| Marker `success`; `main-watcher-test` `failure`; CTRF missing, invalid, or not downloadable after retries | Red; "failing tests unknown", with a link to the run |
| Marker `success`; `main-watcher-test` `success` | Green; a warning if CTRF is missing or lists failures |
| Marker `success`; `main-watcher-test` missing, or any other conclusion | Contract error: `neutral`, alert "outcome contract broken" |

Any other error reading the jobs API leaves the report pending, to be retried (point 3).

**2. Order of writes.** The Reporter writes the issue side first and completes the check
run last:
- **Red:** create or update the lock issue, then complete the check run as `failure`.
- **Green:** close the open lock issue, if any, then complete the check run as `success`.
- **Infrastructure error:** raise the de-duplicated `watcher-infra` alert, then complete
  the check run as `neutral`.

**3. Reporting pending, tied to the test job.** A check run that is still `in_progress`
while the `main-watcher` job of its target run (`external_id`) has completed means
**reporting pending**.
- **Only that job matters.** It holds the test step, the marker step and the CTRF upload,
  and the upload steps have short timeouts, so it ends soon after the tests. Other jobs in
  the run, such as the `report` job that publishes the timing summary (ADR-011), are
  ignored. A seventh review on 2026-09-15 found that waiting for the whole run let a stuck
  `report` job turn a proven failure neutral.
- **Why the job, not just the marker step.** The Reporter waits for the job to complete so
  the CTRF upload has finished or timed out before it reads the artifact. Point 5 covers a
  job that never completes.
- **Detection.** The worker reads the job through the Actions jobs API (`mw-observer`
  already has Actions: read) and flags a pending report as work (ADR-010), so a Reporter
  that stopped part-way runs again on the next cycle. If the job cannot be read because of
  an API error, nothing changes, and it is read again on the next cycle.

**4. Idempotent replay that respects overrides.**
- The issue body's hidden marker records `reported_check=<id>` and `reported_sha=<commit>`.
  Each Reporter comment carries `<!-- main-watcher check=<id> -->`.
- Before any write, the Reporter lists App-authored `main-broken` issues **in any state**,
  updated within `reconcile_lookback` (the same list ADR-015 uses). It then looks for the
  current check run ID in their markers and comments.
  - **Found on an open issue:** writes already made are skipped, and the rest are finished.
  - **Found on a closed issue:** the lock was written and has been closed since. The
    Reporter creates nothing and completes the check run. If a human closed it, that is an
    override of this result (ADR-004), and ADR-015's pass posts the override comment.
- **An override covers its commit.** A red result never creates a lock when the most
  recent App-authored lock was closed by a human and its `reported_sha` is the commit
  under test. Only a failure on a different, newer head locks again (ADR-004). This also
  covers a retest of the same head after an infrastructure error.
- "Latest only" means no other check run for the target can overwrite `reported_check`
  while this one is pending.
- If the Reporter ever finds two open locks, it keeps the older one and closes the newer as
  a duplicate.

**5. A stale run is stopped before it is judged (amends ADR-010).** A completed
`main-watcher` job always goes through the table in point 1, whatever other jobs in the run
are doing.

**Two deadlines.** A job that has not completed has two separate deadlines, because waiting
for a runner is not running. An eighth review on 2026-09-15 found that a single deadline
counted from the check run's creation could abandon a job that started late and was still
testing.
- **Queue deadline:** the job has not started 30 minutes after the check run
  was created.
- **Run deadline:** the job started, and its `started_at` plus its `timeout-minutes` plus 10
  minutes has passed. GitHub's own job timeout normally ends the job well before this.

**Stopping the run.** When either deadline passes, the Planner does not complete the check
run yet:
1. It writes `cancel_requested=<time>` into the check run's output, then cancels the target
   run through the Actions API (`main-watcher` has Actions: write). Cancelling is
   idempotent, so a later run simply asks again.
2. The check run stays `in_progress` until the run has stopped, so ADR-017 starts no second
   test meanwhile.
3. If the run has not stopped 15 minutes after `cancel_requested`, it writes
   `force_cancel_requested=<time>` and calls GitHub's force-cancel endpoint.
4. Once the `main-watcher` job has completed, it goes through the table in point 1 like any
   other job: reported from the test step if `main-watcher-tests-finished` succeeded,
   otherwise `neutral` with an alert.
5. If the run still has not stopped 15 minutes after `force_cancel_requested`, the check
   run is **still not completed**. A ninth review on 2026-09-15 found that completing it
   would let a second test overlap the running one, and would discard a later proven
   failure. So:
   - the check run stays `in_progress`, and no test starts for the target: no retry, no
     newer head, and no forced dispatch (ADR-017);
   - the Planner raises a `watcher-infra` alert, "target run could not be stopped", and
     repeats the force-cancel on each cycle;
   - as soon as the run stops, its job goes through the table in point 1;
   - a person can delete the run instead. That gives "outcome unknown" (404) and releases
     the target.

All of this state is in GitHub: the check run's creation time and output, and the job's
`started_at` and status. A crash at any step is resumed on the next cycle.

**Other cases.**
- **If the job cannot be read** because of an API error, nothing changes, and it is checked
  again on the next cycle.
- **If the run no longer exists** (404), there is nothing to stop: `neutral` with "outcome
  unknown", as in point 1.

A missing CTRF artifact never makes a result neutral, and a pending report is never
discarded as stale.

**6. Stuck reporting is visible.** When reporting has been pending for more than 15 minutes,
the worker raises a `watcher-infra` alert, "reporting pending" (ADR-012).
No newer head is tested while a report is pending, so this alert also covers stalled
detection.

## Options considered

### Option A — Complete the check run last; the in-progress check run is the pending state *(chosen)*

It uses a signal the worker already reads, needs no new record to discover pending work,
and makes check-run completion the single commit point of a report.

### Option B — A separate "reporting pending" record, written before the check run completes

For example, a marker in a watcher-repo issue, or a second check run. It keeps the
original write order. It lost because it adds a record that must be discovered, kept in
step and cleaned up, while Option A gives the same guarantee without one.

### Option C — Keep the order; the hourly sweep opens missing locks

The sweep would compare recent `failure` check runs with lock issues and open any that are
missing. It needs no change to the Reporter. It lost for two reasons: it depends on the
hourly schedule, which is unreliable (C-7); and a failed check run with no open issue
cannot be told apart from a lock a human already closed as an override.

### Option D — Take the test outcome from a second, small artifact

The test step would write an `outcome.json` artifact next to the CTRF reports. It needs no
step-name contract. It lost because a second upload can fail in exactly the same way as the
CTRF upload, while GitHub records the step conclusion itself.

### Option E — Enforce the deadline only at job level, and trust the test step's conclusion

The test step would have no `timeout-minutes`, so only the job timeout could stop a hung
test. It is simpler: no wrapper and no marker step. It lost because it depends on the
conclusion GitHub gives a step cut off by a job timeout, which this design has not
verified, and a `timeout-minutes` added to the test step later would silently turn every
hung test into a red lock.

## Consequences

**Positive**
- A report interrupted at any point completes on the next cycle, normally within about
  2 × `check_period`.
- Replay is safe, so retries need no special handling.
- A failed test run stays red even when its artifact is lost.
- A human override cannot be undone by a replay or a retest of the same commit.
- No new datastore and no new permission: the `main-watcher` App can already read runs.

**Negative**
- **The commit's check turns red slightly later.** It stays `in_progress` until the issue
  write succeeds.
- **A stuck report blocks new tests** for that target until it succeeds. The 15-minute
  alert makes this visible but does not prevent it (R-20).
- **A contract on names.** The Reporter and the reusable workflow must agree on the job
  name `main-watcher` and the step names `main-watcher-test` and
  `main-watcher-tests-finished`. Renaming any of them breaks reporting for targets on that
  workflow tag; this shows up as neutral results and alerts, never as a wrong red or green. Both are versioned by the platform team, and the Reporter
  must handle every workflow tag still pinned by a target.
- **Restore failures caused by the code**, such as a bad package reference, count as
  infrastructure errors and do not lock. They alert after two in a row.
- **More Reporter logic and API calls:** a jobs API read per report, a list of issues in
  any state before writing, marker checks, and duplicate handling.
- **The worker makes one Actions jobs API call** per running test on every cycle, to see
  whether the `main-watcher` job has completed (R-13).
- **Slower recovery from a stuck run.** A stale run holds its check run open through the
  queue or run deadline and the cancel and force-cancel waits, before a retest can start.
  A run that GitHub cannot stop blocks all testing on that target until it stops or someone
  deletes it (R-24).
- **The Planner now cancels target runs**, a further use of `actions: write` on targets
  (R-11).
- **It relies on the marker step's condition.** That GitHub skips the marker step when
  `finished` was never set, including after a cancellation or a lost runner, is
  `[assumption]`. TS-S16 checks it.
- **A narrow window stays neutral.** A run cancelled in the seconds between the test step
  and the marker step is neutral although the tests finished. ADR-017 retests the head.
- **A test that hangs past its deadline never locks**, even when the code causes the hang.
  It raises an alert, and ADR-017 retries up to 3 times before "head untestable".
- **One more piece to maintain:** the wrapper script and its process-tree handling on the
  runner's operating system.
- **Duplicate detection assumes the issues list shows a just-created issue.** If the list
  lags, a replay could create a second issue; the duplicate rule closes it on the next
  pass.

**Follow-on work**
- The `main-watcher-test` step and the always-run upload in the reusable workflow.
- A fault-injection switch in the sandbox Reporter and a switch that fails the upload step.
- The worker's "reporting pending" alert.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Reusable workflow step layout; Reporter outcome table, write order and replay checks; worker stale and pending rules | TS-S14, TS-S16, TS-U8, TS-U11, TS-U14, TS-U15 | `watcher-infra` alerts "reporting pending", "outcome unknown" and "outcome contract broken" |

## Revisit when

- The "reporting pending" alert fires more than once a month.
- Duplicate lock issues or comments are seen in practice.
- A real failure is reported as neutral, including after a cancellation or timeout later
  in the run, or a restore failure caused by the code goes unnoticed.
