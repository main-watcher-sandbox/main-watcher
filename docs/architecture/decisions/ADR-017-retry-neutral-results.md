---
id: ADR-017
type: adr
status: proposed
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "the 'head untestable' alert fires more than once a month, or neutral retries use a noticeable share of Actions minutes"
sources: [FR-2, FR-4, CQ-5, ADR-003, ADR-009, ADR-010, ADR-013, ADR-014]
confidence: assumed
amends: [ADR-003, ADR-010]
---

# ADR-017 — A head whose newest result is neutral is tested again after a wait (amends ADR-003 and ADR-010)

**Deciders:** platform team; requester confirmation pending (CQ-14) · **Consulted:** —

## Context

ADR-010 flags a head for testing only when it has **no** Main Watcher check run. ADR-003
treats a check run's presence as "tested", and the Planner never starts a head that has
one. That works while every completed check run is a real result.

A `neutral` result is not a real result. It means the head was not tested: a setup
failure, a cancelled or stale run, an outcome contract error, or a deleted run (CQ-5,
ADR-013). An adversarial review of the architecture on 2026-09-15 found:

- after a neutral result on an unchanged head, neither the worker nor the Planner ever
  tests that head again;
- ARCH-001 §5.1 promised a retest after a cancelled run, but as a separate step after
  marking the run neutral, so a crash between the two would lose it;
- until someone pushes, a broken `main` stays unlocked, or a fixed `main` stays locked
  while the watcher keeps renewing the lease (ADR-014).

## Decision

**1. One eligibility rule.** The worker and the Planner use the same rule, read from the
Main Watcher check runs on the head commit. A head is **eligible** for a test when either:
- it has no check run, and the target's last test started more than `poll_interval` ago
  (unchanged from ADR-010); or
- its newest check run completed as `neutral` more than `poll_interval` ago, and the head
  has fewer than 3 `neutral` check runs `[unconfirmed]`.

A head whose newest check run is `in_progress`, `success` or `failure` is not eligible.

**2. No separate retry step.** Completing a check run as `neutral` is the whole of the
failure handling. The retest follows from the rule on a later cycle, so a crash at any point
after the neutral write cannot lose it. Each attempt gets its own check run, and the
**newest** check run on a commit is that commit's result (amends ADR-003).

**3. Checked again before starting.** The Planner re-reads the head's check runs just
before creating a new one. `watch.yml` runs one at a time, so two runs cannot both start the
same retry.

**4. Giving up.** After the third neutral result on the same head, the head is no longer
eligible. The Planner raises a de-duplicated `watcher-infra` alert, "head untestable",
naming the head and any open lock, because that lock cannot now close automatically. Testing
resumes when:
- a push creates a new head, which is eligible; or
- someone dispatches `watch.yml` for the target with `force: true`, which tests the current
  head regardless of the cap, but never while a test is in progress.

## Options considered

### Option A — Neutral results stay eligible, by a rule read from check runs *(chosen)*

It heals transient infrastructure errors without a person, survives crashes, and needs no
new state.

### Option B — Retest immediately, in the same run that marks the result neutral

This is what ARCH-001 §5.1 described. It is simpler to read. It lost because a crash between
the two writes loses the retest, and retrying at once, without a wait, repeats the failure
while a feed or runner outage is still in progress.

### Option C — A retry marker that schedules the next attempt

For example, a timestamp in the check run's output or in a watcher-repo issue. It lost
because it is another record to keep in step, when the check runs already hold everything
the rule needs.

### Option D — Never retry automatically; alert and wait for a push

It is the simplest option. It lost because a short runner or feed outage would leave a lock
stuck, or a failure unreported, until someone happens to push. That works against FR-2 and
the correctness of the lock.

## Consequences

**Positive**
- Transient infrastructure errors resolve without a person.
- A crash cannot lose a retry.
- No new state and no new permission.

**Negative**
- **Extra test runs.** A persistent infrastructure problem costs up to 3 test runs per
  head, in the target's Actions minutes.
- **Slower recovery.** Each retry waits `poll_interval` (15 min by default).
- **Several check runs with the same name on one commit.** GitHub's UI shows the newest;
  the Reporter's walk-back for the last green commit must also use the newest.
- **After the cap, a stale lock stays** until someone pushes, dispatches with `force`, or
  overrides it (R-23).

**Follow-on work**
- A shared eligibility function with one test suite used by the worker and the Planner.
- The `force` input on `watch.yml`.
- The "head untestable" alert.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Shared eligibility rule in the worker and Planner; Planner re-check before starting | TS-S18, TS-U13 | `watcher-infra` alert "head untestable" |

## Revisit when

- The "head untestable" alert fires more than once a month.
- Neutral retries use a noticeable share of Actions minutes.
