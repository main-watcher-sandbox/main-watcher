---
owner: platform-team
reviewed: 2026-09-17
review_by: 2027-03-15
---

# Issue #16 sandbox validation

Validated on 2026-09-17 with the sweep pushed to `main-watcher-sandbox/main-watcher` (the
private watcher replica) as
[`09d4b44`](https://github.com/main-watcher-sandbox/main-watcher/commit/09d4b44844a96b6ff439a6c477662c85dd147d0a)
at 21:23Z, whose tree is MainWatcher `982dbd3`. The target is
`main-watcher-sandbox/sample-target` with `poll_interval: 1`. The trigger worker was scaled to
zero throughout (`kubectl -n main-watcher-sandbox get deploy trigger-worker` → 0 replicas), so
nothing but a sweep or a hand-run cycle could act. "By hand" means the `pat-actium` account.

GitHub ran the schedule on the third slot, at 00:19:03Z, and skipped the first two; see
[below](#the-schedule-itself). The sweeps recorded in the two sections after this one were
dispatched by hand with no `target`, which is the same run as a scheduled one in everything but
the event: the same `sweep` run name, the same `targets` and matrix jobs, and `MW_SWEEP: true`.

## Shape: a run with no target sweeps every enabled target

[Run 35276541006](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35276541006),
21:25Z.

| Step | Evidence |
| --- | --- |
| Run name | `sweep`, not `watch <target>`, so the worker never counts it as a target's cycle |
| `targets` job, 21:25:44Z → 21:26:04Z | `--list-targets` gave `["main-watcher-sandbox/sample-target"]` without a token |
| `watch` matrix leg | One leg, `watch (main-watcher-sandbox/sample-target)`, `MW_SWEEP: true` |
| The leg is an ordinary cycle | `Started check 105388936455, target run 35276702018` |
| No alert | The push it tested was 2 minutes old, well inside the 15-minute threshold |
| `Sweep: no gate failed open.` | No merge-group gate run in the past hour |

## TS-S11: with the worker scaled to 0, a waiting push is tested by the sweep and the alert is raised

| Time (UTC) | Event |
| --- | --- |
| 21:28:15Z | A cycle dispatched by hand reported the previous check; the target was left clean |
| 21:29:35Z | Push [`96c87c4`](https://github.com/main-watcher-sandbox/sample-target/commit/96c87c4ce9ade84f59f518d54c2efcd0a28c6775) to `main`. Nothing tested it: the worker was down, and no cycle was dispatched after 21:28 |
| 22:17Z, 23:17Z | Both scheduled slots passed with no run; GitHub ran the 00:17Z one (below) |
| 23:29:36Z | Sweep dispatched by hand: [run 35287073574](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35287073574) |
| 23:30:46Z | `Sweep: the trigger worker appears down; head 96c87c4 is eligible for a test.` → alert [main-watcher#10](https://github.com/main-watcher-sandbox/main-watcher/issues/10) |
| 23:30:50Z | `Started check 105421959319, target run 35287169685` — the waiting push is tested by the sweep |
| 23:32:35Z | The target run passed |
| 23:33:48Z | A cycle dispatched by hand reported it: check 105421959319 `success` |

The alert's body dated the work from the push itself, not from the sweep that found it, and
said what it could rule out:

> The hourly sweep found work on `main-watcher-sandbox/sample-target` that has waited 121
> minutes, since 2026-09-17 21:29 UTC: head 96c87c4 is eligible for a test.
>
> No `watch.yml` cycle has been dispatched for it in that time. …

121 minutes is 23:30:46Z minus the 21:29:35Z push, so the work was dated by the activity entry
for that push and not by anything the sweep did. The "no cycle dispatched" line is the read of
the replica's own `watch.yml` runs since 23:15Z, which found none: the 21:28 cycle is an hour
older. The sweep 8 minutes later ([run
35287831053](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35287831053))
raised nothing, because by then the only work was a head pushed 2 minutes earlier.

## `gate-fail-open` check runs raise an alert

An expired lease makes the gate fail open without any API failure (ADR-014), which is the
cheapest way to produce a real `gate-fail-open` check run.

| Time (UTC) | Event |
| --- | --- |
| 23:34:11Z | `sandbox-lock.yml` with `lease_hours=-1` opened App-authored lock [sample-target#29](https://github.com/main-watcher-sandbox/sample-target/issues/29) with a lease an hour in the past |
| 23:35:15Z | PR [sample-target#28](https://github.com/main-watcher-sandbox/sample-target/pull/28) added to the merge queue (`enqueuePullRequest`; the repo has auto-merge off, so `gh pr merge` cannot do it) |
| 23:35:36Z | Merge-group gate [run 35287526099](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35287526099) on `gh-readonly-queue/main/pr-28-96c87c4…`: `main-watcher-gate` `success`, then `main-watcher/gate-fail-open` `success` |
| 23:36:05Z | The group merged, since `slow_check_minutes` is 0 |
| 23:38:04Z | Sweep [run 35287603448](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35287603448): `Sweep: reported 1 merge group(s) whose gate failed open.` → alert [main-watcher#11](https://github.com/main-watcher-sandbox/main-watcher/issues/11) |
| 23:41:11Z | The next sweep found the same run and wrote nothing: alert #11 still has no comments |

The alert listed the run, its queue branch and the merged commit, and carried the marker
`<!-- main-watcher gate-fail-open runs=35287526099 -->`, which is what makes the second sweep
silent. The alert is separate from the lock: the sweep raised it while the lock was open and
`main` was being tested, and a merge during a lock is still reconciliation's job (#20).

The query behind it was also checked against the sandbox's whole gate history: of the 14
merge-group gate runs on this target, the three examined by hand
([35228722242](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35228722242),
35146938518, 35124692434, one passing gate and two failing ones) all carry
`main-watcher/gate-fail-open` with conclusion `skipped`, so a gate that enforced the lock
correctly is never reported.

## The schedule itself

The cron `17 * * * *` reached the replica's default branch at 21:23Z. **The 22:17Z and 23:17Z
slots produced no run of any kind**, and nothing was queued at 22:27Z or 23:29Z. The **00:17Z
slot ran**, as [run 35290670923](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35290670923),
created 00:19:03Z — two minutes late, on the event `schedule`:

| Step | Evidence |
| --- | --- |
| `targets` job, 00:19:07Z → 00:19:29Z | Listed the one enabled target with no input to take it from |
| `watch` matrix leg, 00:19:33Z → 00:20:19Z | `MW_SWEEP: true` on a run nobody dispatched |
| `No eligible head.` | `main` was green and reported, so the sweep correctly did nothing |
| `Sweep: reported 1 merge group(s) whose gate failed open.` | The 23:35Z fail-open was still inside the hour, and alert #11 already carried its marker, so nothing was written: alert #11 still has no comments |

So the schedule works, but two of its first three slots were dropped — which is C-7 measured
rather than assumed, and the reason ADR-010 makes the schedule a backup and not the trigger. A
sweep-only design would have left `main` untested for three hours here; the trigger worker is
what keeps NFR-1.

This run was the replica's `09d4b44`, the tree before the PR #46 review fixes. Those fixes are
in `Program.cs` and `GitHubGateway.cs`; `watch.yml`, which is what the schedule acts on, is
unchanged by them.

## State afterwards

- `main` of `sample-target` is [`a15eeb3`](https://github.com/main-watcher-sandbox/sample-target/commit/a15eeb35), green, with check 105423591210 `success` and nothing pending.
- Lock #29 was closed by the App's own green report, with its closing comment; no target issue is open.
- The probe branch `ts-s11-fail-open` is deleted and PR #28 is merged.
- Alerts #10 and #11 are left open in the replica, as the evidence above.
- The trigger worker is still scaled to zero, as it was found.
