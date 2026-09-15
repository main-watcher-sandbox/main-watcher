---
id: ADR-013
type: adr
status: proposed
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "the 'reporting pending' alert fires more than once a month, or duplicate lock issues or comments are seen"
sources: [FR-3, FR-4, ADR-003, ADR-009, ADR-010]
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

There is no datastore to hold a "pending" flag (ADR-003), so the fix has to use state that
GitHub already holds.

## Decision

**1. Order of writes.** The Reporter writes the issue side first and completes the check
run last:
- **Tests failed:** create or update the lock issue, then complete the check run as
  `failure`.
- **Tests passed:** close the open lock issue, if any, then complete the check run as
  `success`.
- **Infrastructure error:** raise the de-duplicated `watcher-infra` alert, then complete
  the check run as `neutral`.

**2. Reporting pending.** A check run that is still `in_progress` while its target run
(`external_id`) has completed means **reporting pending**. The worker already flags this as
work (ADR-010), so a Reporter that stopped part-way runs again on the next cycle.

**3. Idempotent replay.** Every issue write carries the check run ID, so repeating it
changes nothing:
- the issue body's hidden marker records `reported_check=<id>`, and each Reporter comment
  carries `<!-- main-watcher check=<id> -->`;
- the Reporter skips any create, update, comment or close that already carries the
  current check run ID;
- before creating an issue, it lists open App-authored `main-broken` issues. If it ever
  finds two, it keeps the older one and closes the newer as a duplicate.

**4. Narrower stale-run rule (amends ADR-010).** An in-progress check run is marked
`neutral` as stale only when its target run has not completed, or completed without a CTRF
artifact. A pending report is never thrown away as stale.

**5. Stuck reporting is visible.** When reporting has been pending for more than 15 minutes
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

## Consequences

**Positive**
- A report interrupted at any point completes on the next cycle, normally within about
  2 × `check_period`.
- Replay is safe, so retries need no special handling.
- No new datastore and no new permission.

**Negative**
- **The commit's check turns red slightly later.** It stays `in_progress` until the issue
  write succeeds.
- **A stuck report blocks new tests** for that target until it succeeds. The 15-minute
  alert makes this visible but does not prevent it (R-20).
- **More Reporter logic:** marker checks before every write, and duplicate-issue handling.
- **Duplicate detection assumes the issues list shows a just-created issue.** If the list
  lags, a replay could create a second issue; the duplicate rule closes it on the next
  pass.

**Follow-on work**
- A fault-injection switch in the sandbox Reporter.
- The worker's "reporting pending" alert.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Reporter write order and replay checks; worker stale and pending rules | TS-S14, TS-U8 | `watcher-infra` alert "reporting pending" |

## Revisit when

- The "reporting pending" alert fires more than once a month.
- Duplicate lock issues or comments are seen in practice.
