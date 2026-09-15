---
id: ADR-016
type: adr
status: proposed
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "sandbox test TS-S17 disproves A-7, or reconciliation reports a merge group that merged after its lock opened more than once a quarter"
sources: [FR-4, C-1, A-7, ADR-002, ADR-008, ADR-009, ADR-014, ADR-015]
confidence: assumed
amends: ADR-002
---

# ADR-016 — Opening a lock re-runs the gate for merge groups already in the queue (amends ADR-002)

**Deciders:** platform team; requester confirmation pending (CQ-13) · **Consulted:** security
(a new use of `actions: write`)

## Context

ADR-002's gate decides once, when a merge group's `merge_group` event runs it. GitHub
merges a group as soon as all of its required checks have passed. An adversarial review of
the architecture on 2026-09-15 found this sequence:

1. An unlabelled group passes the gate while no lock exists.
2. It waits for another required check, such as the target's own CI, which can take many
   minutes.
3. Meanwhile the Reporter opens a lock.
4. The other check passes, and the group merges onto a `main` that is now known to be red.

No outage is involved, so ADR-008's fail-open path does not describe it. Reconciliation
reports the merge afterwards, but FR-4 promises that the queue is blocked. The longer a
target's CI, the more groups are exposed each time a lock opens.

The same happens when a lapsed lock is renewed (ADR-014): groups that passed the gate
during the lapse may still be waiting.

GitHub's merge queue has no pause (C-1), so the design cannot hold the queue itself.

## Decision

**1. Queue sweep.** Right after the Reporter opens a lock, and right after the Planner
renews a lapsed lease, `watch.yml` sweeps the target's queue:
- it lists the groups still in the queue, from the temporary `gh-readonly-queue/main/*`
  branches GitHub creates for them `[assumption]` (A-7);
- for each group, it finds the gate workflow runs for the group's head commit;
- a gate run that **completed successfully** and **started before** the sweep moment (the
  lock's creation, or the lease renewal) is re-run through the Actions API. The re-run
  reads the current lock, so it fails the group unless every PR in it has `fixes-main`;
- a gate run that started before that moment and is **still running** may already have
  read "no lock". It is re-run as soon as it completes;
- a gate run that started after that moment already sees the lock, and is left alone.

"Started before" is used rather than "completed before", because a gate can read the lock
at any point during its run.

**2. Durable progress.** The sweep's state lives in the lock issue's hidden marker,
`queue_swept=<timestamp>`. It is written only when no queued group has an unhandled gate
run from before the sweep moment. Until then:
- the worker flags work for the target, since it can read the marker (ADR-014);
- each watcher run continues the sweep;
- if the sweep is still unfinished after 15 minutes `[unconfirmed]`, the worker raises a
  `watcher-infra` alert, "queue sweep unfinished".

**3. What the re-run relies on (A-7).** A re-run of a successful required check makes that
check pending again for the group, so the queue waits; when the re-run fails, the queue
removes the group. This is `[assumption]` until sandbox test TS-S17 confirms it, which must
happen before rollout. If it proves false, Option B or Option D replaces this decision.

**4. FR-4, narrowed.** A group can still merge in the short window between the lock
opening and its gate re-run taking effect. That is normally seconds, and at most one
`check_period` for a gate that was still running. Such merges are not prevented; they fall
inside the lock window, so reconciliation reports them (ADR-008, ADR-015). FR-4 therefore
becomes `[unconfirmed]` (CQ-13):

> While a lock is open, no group whose gate ran after the lock opened merges unless every
> PR in it is a fix. Groups already queued when the lock opened are re-checked, and any that
> merge before the re-check takes effect are reported.

**5. Permissions.** None new. `main-watcher` already has Actions: write and Contents: read
on targets (ADR-009); re-running the gate is a new use of the first.

## Options considered

### Option A — Re-run the gate for groups queued before the lock *(chosen)*

It costs API calls only when a lock opens, and changes nothing for merges on a healthy
`main` (NFR-2).

### Option B — The gate waits to decide until every other required check has finished

The gate would poll the group's other required checks and read the lock only once they are
done. It is better in two ways: it does not depend on re-run behaviour (A-7), and the
remaining window is only the queue's merge latency. It lost because it holds a runner for
the full CI duration of every merge group, including on a healthy `main`; it needs to read
the repository's rulesets to know which checks to wait for; and it still leaves a window of
seconds.

### Option C — Narrow FR-4 only, and report groups that passed before the lock

It is the simplest option, with no new mechanism. It lost as the whole answer because, with
long CI, a lock that opens while several groups are waiting lets all of them merge onto a
red `main` during normal operation. It is kept for the remaining window (point 4).

### Option D — Remove queued PRs directly with the GraphQL `dequeuePullRequest` mutation

It acts on the queue without depending on how check re-runs behave. It lost because the App
would need further write permission on pull requests `[assumption]`, widening what a leaked
key could do (R-11), and it would remove fixes too unless it re-implemented the gate's
label rule.

## Consequences

**Positive**
- Groups waiting on other checks when a lock opens are stopped, not just reported.
- No cost and no delay on a healthy `main`.
- No new permission.
- Groups that passed during a lease lapse are re-checked too.

**Negative**
- **It depends on A-7**, GitHub behaviour this design has not verified. TS-S17 must pass
  before rollout.
- **It depends on how GitHub names temporary merge-queue branches.** If that changes, the
  sweep finds no groups; they merge and are reported instead (R-22).
- **A window of seconds remains**, in which a non-fix group is reported but not prevented
  (R-22).
- **Re-runs add noise** in the target's Actions tab, and the unlock comment must also list
  groups removed by a re-run.
- **One more marker, one more worker rule, and one more alert.**

**Follow-on work**
- The queue sweep in the Reporter and Planner, and the worker's unfinished-sweep rule.
- TS-S17 in the sandbox, before rollout.
- Security review of re-running target workflows with `main-watcher`.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Queue sweep in `watch.yml`; worker unfinished-sweep rule | TS-S17, TS-U12 | `watcher-infra` alert "queue sweep unfinished"; "Merged while locked" reports |

## Revisit when

- TS-S17 disproves A-7.
- Groups merging after their lock opened are reported more than once a quarter.
- GitHub ships a native merge-queue pause (C-1).
