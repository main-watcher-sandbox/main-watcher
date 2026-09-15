---
id: ADR-013
type: adr
status: proposed
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "the 'reporting pending' alert fires more than once a month, duplicate lock issues or comments are seen, or a real failure is reported as neutral"
sources: [FR-3, FR-4, ADR-003, ADR-004, ADR-007, ADR-009, ADR-010, ADR-015]
confidence: assumed
amends: [ADR-003, ADR-010]
---

# ADR-013 — The Reporter completes the check run last, so an interrupted report is replayed (amends ADR-003 and ADR-010)

**Deciders:** platform team; requester confirmation pending (CQ-10) · **Consulted:** —

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
  with the fixed ID `main-watcher-test`. That step has no `continue-on-error`, so its
  conclusion is the exit code that ADR-007 relies on;
- writes `timings.json` and uploads the CTRF artifact in later steps that run even when
  `main-watcher-test` fails.

Restore counts as setup and build counts as test `[unconfirmed]`: a package-feed outage
should not lock the queue, but a compile break on `main` should.

The Reporter reads the step's conclusion from the Actions jobs API. GitHub records it
whether or not any upload worked. What the Reporter concludes:

| Target run | `main-watcher-test` step | CTRF | Result |
|---|---|---|---|
| Cancelled, or stopped by the job timeout | Any | Any | Infrastructure error: `neutral`, alert |
| Completed | Not run, because a setup step failed | — | Infrastructure error: `neutral`, alert |
| Completed | `failure` | Valid | Red; failing tests listed |
| Completed | `failure` | Missing, invalid, or not downloadable after retries | Red; "failing tests unknown", with a link to the run |
| Completed | `success` | Any | Green; a warning if CTRF is missing or lists failures |
| Deleted after completing (the API returns 404) | — | — | `neutral`, alert "outcome unknown" |

Any other error reading the jobs API leaves the report pending, to be retried (point 3).

**2. Order of writes.** The Reporter writes the issue side first and completes the check
run last:
- **Red:** create or update the lock issue, then complete the check run as `failure`.
- **Green:** close the open lock issue, if any, then complete the check run as `success`.
- **Infrastructure error:** raise the de-duplicated `watcher-infra` alert, then complete
  the check run as `neutral`.

**3. Reporting pending.** A check run that is still `in_progress` while its target run
(`external_id`) has completed means **reporting pending**. The worker already flags this as
work (ADR-010), so a Reporter that stopped part-way runs again on the next cycle.

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

**5. Narrower stale-run rule (amends ADR-010).** An in-progress check run is marked
`neutral` as stale only when its target run has not completed within the target's timeout
plus 10 minutes, or no longer exists. A completed target run always goes through the table
in point 1. A missing CTRF artifact never makes a result neutral, and a pending report is
never discarded as stale.

**6. Stuck reporting is visible.** When reporting has been pending for more than 15 minutes
`[unconfirmed]`, the worker raises a `watcher-infra` alert, "reporting pending" (ADR-012).
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
step-ID contract. It lost because a second upload can fail in exactly the same way as the
CTRF upload, while GitHub records the step conclusion itself.

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
- **A contract on a step ID.** The Reporter and the reusable workflow must agree on
  `main-watcher-test`. Both are versioned by the platform team, and the reporter must
  handle every workflow tag still pinned by a target.
- **Restore failures caused by the code**, such as a bad package reference, count as
  infrastructure errors and do not lock. They alert after two in a row.
- **More Reporter logic and API calls:** a jobs API read per report, a list of issues in
  any state before writing, marker checks, and duplicate handling.
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
| Reusable workflow step layout; Reporter outcome table, write order and replay checks; worker stale and pending rules | TS-S14, TS-S16, TS-U8, TS-U11 | `watcher-infra` alerts "reporting pending" and "outcome unknown" |

## Revisit when

- The "reporting pending" alert fires more than once a month.
- Duplicate lock issues or comments are seen in practice.
- A real failure is reported as neutral, or a restore failure caused by the code goes
  unnoticed.
