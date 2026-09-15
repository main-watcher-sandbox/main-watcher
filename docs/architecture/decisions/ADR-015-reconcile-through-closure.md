---
id: ADR-015
type: adr
status: proposed
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "a closed lock is found unreconciled more than 24 h after closing, or a watcher outage lasts longer than reconcile_lookback"
sources: [NFR-4, ADR-003, ADR-004, ADR-008]
confidence: assumed
amends: ADR-008
---

# ADR-015 — Reconciliation follows each lock through its closure, not only while it is open (amends ADR-008)

**Deciders:** platform team; requester confirmation pending (CQ-12) · **Consulted:** —

## Context

Under ADR-008, each watcher run reconciles **only targets with an open lock issue**,
reading merges since the `last_reconciled` marker. ADR-004 lets a human close a lock at
any time.

An adversarial review of the architecture on 2026-09-15 found that these two rules
together can lose reports:

1. An unlabelled PR merges during a lock. This can happen through a gate fail-open, a
   lapsed lease (ADR-014), or in the minutes before the next watcher run.
2. A human closes the lock issue before the watcher processes that merge.
3. From then on, no watcher run looks at the issue, so the merge is never reported.

That breaks NFR-4, and is most likely during an outage, exactly when the gate's own
best-effort signal may also be missing.

A lock ends either when the App closes it after a green run, or when a human overrides it.
Either way the lock window ends, but reconciliation of that window should not.

## Decision

**1. Lock window.** A lock's window runs from the issue's creation to its closure, or to
now while it is open. Every unlabelled PR merged inside the window is reported.

**2. Discovery that does not depend on the issue being open.** Each watcher run lists
App-authored `main-broken` issues in any state, updated within `reconcile_lookback`
(30 days `[unconfirmed]`). It processes every issue whose marker does not yet say
`reconciled=complete`.

**3. A cursor per incident.** `last_reconciled` remains the activity cursor in each issue's
hidden marker (ADR-003). For a closed issue, the Planner reads activity up to the issue's
`closed_at` and reports what it finds. Only after all of that succeeds does it write
`reconciled=complete`, so a failure part-way is retried on the next run.

**4. Reporting on a closed issue.** A merge found in a closed lock's window is:
- appended to the issue body under "Merged while locked";
- posted as a comment, so the issue's participants are notified;
- raised as a `watcher-infra` alert.

The App still never reopens the issue (ADR-004).

**5. One pass for closures.** The pass that finishes a human-closed issue also posts the
ADR-004 override comment, if it has not been posted yet. App-closed issues go through the
same pass, so the Reporter needs no special step before closing.

**6. Worker trigger.** The worker flags **work** for a target with a closed App-authored
lock that is not yet `reconciled=complete`, using the Issues: read permission from
ADR-014.

**7. Persistent failure.** If a closed lock still cannot be reconciled 24 hours after it
closed, the Planner raises a `watcher-infra` alert.

## Options considered

### Option A — A cursor per incident, reconciled through the closure *(chosen)*

It keeps state in the lock issue itself, where ADR-003 already puts it, and makes "fully
reconciled" an explicit, checkable fact per incident.

### Option B — A cursor per target, independent of lock issues

For example, a marker in a watcher-repo issue, or a check run on `main` recording the last
activity checked for the target. It is better in one way: it has no lookback window, so
merges from any age can be found. It lost because it still has to rebuild lock windows
from issue events, and it adds a second state record per target alongside the lock issue.

### Option C — Stop a human close from ending the lock before reconciliation

For example, the App reopens the issue until reconciliation finishes, or overrides use a
label instead of closing. It lost because it contradicts ADR-004 (the App never reopens),
and makes the override slower and less obvious at the moment people are already blocked.

## Consequences

**Positive**
- Every unlabelled merge inside a lock window is reported, however the lock ended.
- No new state store or permission beyond ADR-014.
- The override comment and the final reconciliation happen in one place.

**Negative**
- **More API calls:** a list of issues in any state on each run, and activity reads for
  recently closed locks until they are complete.
- **Reports can land on closed issues**, which fewer people watch. The `watcher-infra`
  alert is the dependable channel.
- **A watcher outage longer than `reconcile_lookback`** means older closures are never
  reconciled (R-21). This is accepted; the worker and sweep alerts fire long before that.
- **Boundary precision.** Activity times and `closed_at` are compared at one-second
  resolution; a merge in the same second as the close counts as inside the window.

**Follow-on work**
- Planner discovery over open and closed locks.
- The worker's closed-lock rule.
- The "reconciliation failing" alert.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Planner reconciliation over open and closed locks; worker closed-lock rule | TS-S15, TS-U4, TS-U10 | `watcher-infra` alerts "Merged while locked" and "reconciliation failing" |

## Revisit when

- A closed lock is found unreconciled more than 24 hours after it closed.
- A watcher outage lasts longer than `reconcile_lookback`.
