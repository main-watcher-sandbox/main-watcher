---
id: ADR-020
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-23
review_by: 2027-03-15
review_trigger: "the 'watch.yml run stuck' alert fires more than once a month, a stuck `sweep` run blocks a target, or a production `reporter` environment needs required reviewers"
sources: [FR-2, FR-3, ADR-003, ADR-010, ADR-012, ADR-013]
confidence: confirmed
amends: [ADR-010, ADR-013]
---

# ADR-020 — The worker cancels a `watch.yml` run that has not started after 20 minutes (amends ADR-010 and ADR-013)

**Deciders:** requester (confirmed 2026-09-23, MainWatcher#67), platform team · **Consulted:** —

## Context

The worker starts no `watch.yml` run for a target while an earlier one is queued or running, read
from the run name (ADR-010, `TriggerCycle`). That is right: it stops a queue of duplicate cycles
building up. But it means a run that never starts blocks every later cycle for its target. No test is
started, no report is written, and the check run stays `in_progress`.

Run 11 of the scenario suite, on 2026-09-23, found such a run (MainWatcher#67). A `watch.yml` run for
`sample-target-5` sat in GitHub's `waiting` state for 25 minutes, held at the gate for the `reporter`
environment. That environment has no reviewers and no wait timer. The run's pending deployment listed
no reviewers, and said `current_user_can_approve: false`: it was waiting for an approval nobody could
give. The run would have waited indefinitely: a job's `timeout-minutes` counts only once it has
started, and GitHub holds a job at an environment gate for up to 30 days. The "reporting pending"
alert (ADR-013 point 6) fired after 15 minutes, as designed. Nothing cleared the blockage until a
person cancelled the run. The worker's "no run completed in 2 h" alert did not fire either, since
other targets' runs were completing normally.

For a **target** run that will not start or stop, Main Watcher already has a stale-run lifecycle:
queue and run deadlines, cancel, force-cancel, alert (ADR-013 point 5). It had nothing of the kind
for its **own** runs.

## Decision

**1. The `reporter` environment must have no required reviewers.** It exists only to keep the main
App key away from other workflows (ARCH-001 §8). The worker can start a cycle every minute, so an
approval on every cycle does not fit an unattended watcher. "No required reviewers" is an install
requirement, written into `docs/watcher.md` and `docs/onboarding.md`. A wait timer is allowed, and
is added to the deadline.

**2. The deadline is 20 minutes from the run's `created_at`, plus the environment's wait timer.**
It applies to a `watch.yml` run for a target (named `watch <owner/repo>`) that has not completed and
is not `in_progress`: `waiting`, `queued`, `pending` or `requested`. It is a single constant, not a
target setting. 20 minutes clears the legitimate wait behind a sweep's job for the same target, whose
job timeout is 15 minutes. The deadline is computed from GitHub's own times, so it needs no state of
the worker's and survives a restart (ADR-003).

**3. The worker acts, and needs no new permission.** It already lists these runs through
`mw-observer` on every cycle. It cancels through `mw-doorbell`, whose Actions: write on the watcher
repo covers cancel and force-cancel, which §8 already lists as the worst case if that key leaks. Before
cancelling a `waiting` run, the worker reads the run's `pending_deployments`:
- **Reviewers listed:** the run is waiting for a person. The worker does not cancel it. It raises an
  alert that names the reviewers and the install requirement it breaks.
- **No reviewers listed**, or a run that is not `waiting`: the worker applies point 4.

Whether `mw-observer`'s Actions: read can read `pending_deployments` with an installation token is
`[assumption]`, to be checked before the build. If it cannot, `mw-doorbell` reads it, since it holds
Actions: write on the same repository.

**4. Stopping the run follows ADR-013 point 5.**
1. At the deadline, the worker raises a `watcher-infra` alert, "watch.yml run stuck `<state>` on
   `<target>`", and cancels the run.
2. If the run has not stopped 15 minutes after the deadline, the worker force-cancels it.
3. If it has not stopped 15 minutes after that, the worker raises "watch.yml run could not be
   stopped on `<target>`", and repeats the force-cancel on each cycle.

The times are counted from the deadline, not from when a cancel was sent, so no cancel needs to be
recorded. Cancelling is idempotent, so a cycle that repeats a step does no harm. Alerts are raised and
de-duplicated as the worker's other alerts are (ADR-012): once when the condition starts to hold, and
at most once an hour while it goes on.

**5. Re-dispatch needs no new logic, and has no limit.** Once the stuck run has completed, it no
longer marks its target active, so the next cycle finds the target's work and dispatches again. If
each new run gets stuck too, each is cancelled in turn. Each round costs a few requests every 20
minutes or so, and it recovers by itself once GitHub clears the fault. A persistent fault shows up as
the de-duplicated alert.

**6. Sweep runs are out of scope.** A run named `sweep` is never cancelled by this rule. If a sweep's
job for a target gets stuck, it holds that target's concurrency group, so each dispatched run for that
target waits `pending` behind it. Those runs are caught and cancelled under point 4 again and again,
and the alert names them as stuck `pending`. A person then has to cancel the sweep. The sweep is a
backup that runs hourly, and this case was not seen, so it was left out; the review trigger covers it
if it occurs.

## Options considered

### Option A — The worker cancels stuck runs of its own, after a deadline *(chosen)*

This option applies the target runs' lifecycle to the watcher's own runs. It uses state the worker
already reads and a permission it already holds.

### Option B — Alert only

This option names the fault sooner, and was proposed in the issue as a cheaper first step. It lost
because a person would still have to find and cancel the run, and the acceptance criteria require that
a stuck run does not block its target indefinitely. The alert is kept as the first step of Option A.

### Option C — The hourly sweep cancels stuck runs

This option makes no worker change. It lost because the sweep's own jobs run in the same `reporter`
environment and can get stuck the same way. It also runs at most hourly, and often late (C-7).

### Option D — Drop the `reporter` environment, and hold the key as a repository secret

This option removes the failure mode entirely: with no environment, there is no gate. It lost because
an environment secret reaches only the jobs that name the environment, while a repository secret
reaches any workflow in the repository. That would weaken how the main App key is scoped (§8), which
TS-S8 checks.

### Option E — Support required reviewers, and never cancel a waiting run

This option leaves every `waiting` run alone and only alerts. It lost because a reviewer gate on every
cycle makes the watcher unusable, and treating every `waiting` run as legitimate would not have
cleared run 11's stuck run.

## Consequences

**Positive**
- A `watch.yml` run that cannot start blocks its target for about 20 minutes, not indefinitely.
- The condition is named in an alert, rather than inferred from "reporting pending".
- No new permission, and no new state.

**Negative**
- **A run that would have started late is cancelled.** A run legitimately held for more than 20
  minutes, such as one behind an unusually slow queue for GitHub-hosted runners, is cancelled and
  dispatched again. The target's report is delayed, never lost.
- **A stuck sweep still needs a person.** See point 6.
- **One more reason for a `watcher-infra` issue.** Each is de-duplicated as the others are.

**Follow-on work**
- MainWatcher#67 builds it: the worker's rule and alerts, the install requirement in
  `docs/watcher.md` and `docs/onboarding.md`, TS-U17 and TS-S19.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| `TriggerCycle`, the worker's alerts (`WorkerAlerts`), the gateway's cancel, force-cancel and `pending_deployments` calls | TS-U17; TS-S19, recorded in `sandbox/issue-67-validation.md` | "watch.yml run stuck" and "could not be stopped" alerts |

GitHub's "approval nobody can give" fault cannot be reproduced on demand, so the path with no
reviewers rests on TS-U17 until it recurs. TS-S19 exercises the rest against GitHub: a real reviewer
gate, the deadline and the alert.

## Revisit when

- The "watch.yml run stuck" alert fires more than once a month.
- A stuck `sweep` run blocks a target.
- A production `reporter` environment needs required reviewers.
