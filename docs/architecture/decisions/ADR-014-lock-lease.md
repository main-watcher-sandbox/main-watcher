---
id: ADR-014
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "a lock lapses more than once a quarter, or the requester prefers enforcing locks through watcher outages"
sources: [FR-4, NFR-3, ADR-002, ADR-004, ADR-008, ADR-010]
confidence: confirmed
amends: [ADR-002, ADR-008, ADR-010]
---

# ADR-014 — A lock is enforced only while the watcher keeps renewing its lease (amends ADR-002, ADR-008 and ADR-010)

**Deciders:** requester (confirmed 2026-09-15, CQ-11), platform team · **Consulted:** —

## Context

NFR-3 says a watcher or worker outage must not block merges. ADR-008 makes the gate fail
open when it cannot read the lock. An adversarial review of the architecture on 2026-09-15
found a case that rule does not cover:

- the watcher opens a lock and then stops, for example because the worker and the sweep
  are both down, the App key is revoked, or workflows are disabled;
- the GitHub API stays healthy, so the gate keeps reading the open lock and failing every
  ordinary merge group;
- even after a `fixes-main` PR merges, nothing runs the tests that would close the lock.

An open lock issue says nothing about whether anyone still maintains it. The only escape
was a human closing it, which counts as an override (ADR-004). TS-S7 only tested an outage
that began with no lock.

## Decision

**1. Lease marker.** Each App-authored lock issue carries `lease_until=<UTC timestamp>` in
its hidden marker. The Reporter sets it to now + `lock_lease` when it creates the issue.
`lock_lease` is set once in the `targets.yml` defaults: 4 hours.

**2. Renewal.**
- Every `watch.yml` run that processes a locked target sets `lease_until` to now +
  `lock_lease`.
- The worker flags **work** for a target whose open lock was last renewed more than 1 hour
  ago, so renewal does not depend on the unreliable schedule (C-7).
- The hourly sweep also renews, as a backup.

**3. Gate.** On `merge_group`, the gate enforces an App-authored lock only when
`lease_until` is in the future and no more than 24 hours ahead. If the marker is missing,
unreadable, expired or more than 24 hours ahead, the gate:
- passes the merge group;
- emits a `::warning` and a job summary headed **"LOCK LEASE EXPIRED — failed open"**;
- posts a non-required `main-watcher/gate-fail-open` check run with reason
  `lease-expired`. The API is reachable in this case, so this normally succeeds.

The 24-hour cap stops a hand-edited or faulty marker from making the lock unbounded again.

**4. Recovery.** When the watcher renews a lease that had already expired, the same
issue-body write that sets the new `lease_until` also records:
- `lapsed=<old lease_until>..<renewal time>`;
- a new queue-sweep obligation, `sweep_required` (ADR-016).

Because these are written together, a crash right after the renewal cannot hide the lapse.
Afterwards the watcher:
- comments on the lock issue with the lapse window and raises a `watcher-infra` alert,
  "lock lapsed", then writes `lapse_reported=<renewal time>`. If that marker is missing, a
  later run posts them; the comment carries a hidden marker and the alert is de-duplicated
  (ADR-012), so a repeat creates nothing new;
- re-checks merge groups queued during the lapse (ADR-016);
- reconciles merges made during the lapse as usual. The issue stayed open, so they fall
  inside the lock window (ADR-008, ADR-015).

**5. Permission (amends ADR-010).** `mw-observer` gains **Issues: read** on targets, so the
worker can see lock leases. It stays read-only, and the worker makes one more read call
per target per cycle.

## Options considered

### Option A — A lease renewed by the watcher and checked by the gate *(chosen)*

It bounds how long an unmaintained lock blocks merges, and gives the gate no new
credential.

### Option B — The gate checks the watcher's liveness directly

For example, the gate reads the time of the last successful `watch.yml` run in the watcher
repo. It lost because the gate runs with the target repo's `GITHUB_TOKEN`, which cannot
read another repository's runs. Every target would need an App key or token for the
watcher repo, which widens key exposure (§8). Watcher-wide liveness is also a coarser
signal than "this lock is still maintained".

### Option C — Keep enforcing; narrow NFR-3 to exclude existing locks

It is the simplest option and needs no change. It is better on one dimension: it never
unlocks a `main` that is really red. It lost because, after a fix merges during a watcher
outage, every team stays blocked until someone notices and closes the issue, and that
close is recorded as an override. It is the right answer if the requester values lock
correctness above merge availability; that is the question in CQ-11.

## Consequences

**Positive**
- A watcher outage blocks ordinary merges for at most `lock_lease`, even with a lock open.
- No new credential in target repos; the gate still needs only `issues: read`.
- Lapses are visible: gate warning, check run, lock issue comment and alert.

**Negative**
- **A red `main` can unlock during a long watcher outage.** Once `lock_lease` has passed,
  unlabelled PRs can merge onto a broken `main`. They are reported afterwards, not
  prevented (R-19).
- **NFR-3 holds only after a delay** of up to `lock_lease` when a lock is already open.
- **More writes:** about one issue-body edit per locked target per hour. Body edits notify
  nobody, but they appear in the issue's edit history.
- **The worker gains Issues: read** on targets, plus one more API call per target per
  cycle (R-13).
- **Clock dependence.** The gate compares the runner's clock with the marker. A few
  minutes of skew does not matter against a 4-hour lease.
- **Removing the marker by hand unlocks the queue** with a warning instead of an override
  record. This is accepted, because the same person could close the issue.

**Follow-on work**
- Lease check in the gate template.
- Renewal in the Planner, and the renewal rule in the worker.
- `lock_lease` in the `targets.yml` schema.
- The `mw-observer` permission change in the security review.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Gate lease check, Planner renewal, worker renewal rule | TS-S7, TS-U9 | `watcher-infra` alert "lock lapsed"; `gate-fail-open` check runs |

## Revisit when

- Locks lapse more than once a quarter, which points to a watcher availability problem.
- The requester prefers enforcing locks through watcher outages (Option C).
