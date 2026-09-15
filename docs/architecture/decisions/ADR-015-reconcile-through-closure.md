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

# ADR-015 — Reconciliation follows each lock through its closure, and judges labels as they were at merge time (amends ADR-008)

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

**7. Persistent failure.** If any merge in a lock's window stays unresolved for 24 hours,
or a closed lock is still not complete 24 hours after it closed, the Planner raises a
`watcher-infra` alert, "reconciliation failing".

**8. Labels as they were at merge time.** ADR-008 checked each merged PR's **current**
`fixes-main` label. A later review on 2026-09-15 found this is wrong in both directions: a
label added after an unlabelled merge hides the report, and a label removed after a
legitimate fix raises a false alert. The window between merge and reconciliation can be
long, because of outages and the closed-lock pass above. So the Planner:
- reads the PR's `labeled` and `unlabeled` events from the Issues events API, up to the
  PR's `merged_at`;
- treats the PR as a fix only if `fixes-main` was on it at that moment. An event in the
  same second as the merge counts as before it;
- when that history cannot be read, stops at that merge. `last_reconciled` does not move
  past it, the merge is retried on the next run, and the lock is never marked
  `reconciled=complete` while any merge in its window is unresolved.

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

### Option D — Keep judging the PR's current label (ADR-008 as written)

It needs one fewer API call per merge, and it matches what a person sees on the PR today.
It lost because the label can change between the merge and reconciliation, which hides
real reports and raises false ones.

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
- **Label history costs a call per merged PR** in a lock window, and an outage of the
  events API delays reports until it recovers.
- **A label added in the same second as the merge counts as present**, so a report could
  be missed in that exact second.

**Follow-on work**
- Planner discovery over open and closed locks.
- The worker's closed-lock rule.
- The "reconciliation failing" alert.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Planner reconciliation over open and closed locks, with label history; worker closed-lock rule | TS-S15, TS-U4, TS-U10 | `watcher-infra` alerts "Merged while locked" and "reconciliation failing" |

## Revisit when

- A closed lock is found unreconciled more than 24 hours after it closed.
- A watcher outage lasts longer than `reconcile_lookback`.
