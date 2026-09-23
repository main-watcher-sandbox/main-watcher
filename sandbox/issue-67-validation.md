---
owner: platform-team
reviewed: 2026-09-23
review_by: 2027-03-15
---

# Issue #67 sandbox evidence

TS-S19 was run on 2026-09-23 with `sandbox/run-scenarios.sh --only TS-S19 --targets 2`, from `9424fe7` on
`claude/issue-67-stuck-runs`. The suite first put that commit into the sandbox: the gate repo, the replica's
`main`, the worker image rolled out in `main-watcher-sandbox`, and the two pool targets reseeded. Preparing took
5 minutes. **TS-S19 passed** on `main-watcher-sandbox/sample-target` in 28 minutes, with 193 GitHub API calls.

## What was run

The environment every cycle uses, `reporter`, is shared by all targets. A required reviewer on it would hold up
every other unit of the suite. So the sandbox-only switch `MW_SANDBOX_REVIEWED_TARGETS` sends only the listed
targets' cycles to a second environment, `reporter-reviewed`, which has a required reviewer and no secrets. This
is the way `MW_SANDBOX_READ_ONLY_ISSUES` narrows one target's token for TS-S14 (c). The unit makes the environment
itself, with the operator as reviewer, so it depends on no setup done by hand.

| Time (UTC) | What |
| --- | --- |
| 18:28:22 | pushed `d4a3c2d` to the target, with the target in the reviewed list |
| 18:29:23 | the worker's cycle, [run 35902790289](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35902790289), was `waiting` at `reporter-reviewed`; its `pending_deployments` listed the operator, `pat-actium` |
| 18:50:07 | the worker's cycle read the gate, 20 min after the run was created: "watch.yml run 35902790289 is waiting for pat-actium, so it is left alone" |
| 18:50:10 | alert #202, "`watch.yml` run waiting for a reviewer on main-watcher-sandbox/sample-target", raised by `mw-doorbell[bot]`, naming `pat-actium` and the install requirement |
| 18:51:07, 18:52:07 | the next two cycles logged the same and cancelled nothing; no check run existed for `d4a3c2d` |
| 18:52:19 | the unit took the target out of the reviewed list and cancelled the run by hand, as the alert tells a person to |
| 18:53:11 | the worker logged the condition as cleared |
| 18:56:18 | the target's next cycle tested `d4a3c2d` and reported check 107332438346 as `success` |

## What it shows

- **A run waiting for a real approver is left alone** (#67's second acceptance criterion). The worker read the
  gate through `mw-observer` on every cycle past the deadline and never asked to cancel the run. It raised neither
  "`watch.yml` run stuck" nor "could not be stopped".
- **The condition is named in an alert**, raised 21 minutes after the run was created: the 20-minute deadline,
  then the next 60-second cycle. It names the run, the reviewer and the requirement the environment breaks,
  rather than showing up only as a report pending.
- **`mw-observer` reads a real reviewer entry.** The earlier permission check (ADR-020 point 3) had only seen
  empty lists, on completed runs. Here the gateway parsed a `User` reviewer from a live gate.
- **The target recovers once the run is gone.** The next cycle tested the head and reported it, and the alert
  condition cleared without a second alert.

## What it does not show

The cancel path, for a run held with **no** reviewer listed as in #67, run 11, cannot be produced on demand. Nothing
in GitHub makes a gate wait for an approval nobody can give. That path, together with the force-cancel, the
"could not be stopped" alert and the re-dispatch after a stop, rests on TS-U17 (`StuckRunTests`), as ADR-020's
Verification section says. The first real occurrence will show up as "`watch.yml` run stuck `waiting` on
`owner/repo`", with the worker's cancel in its log.
