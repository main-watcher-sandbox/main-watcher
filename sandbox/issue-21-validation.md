---
owner: platform-team
reviewed: 2026-09-18
review_by: 2027-03-15
---

# Issue #21 sandbox validation — the queue sweep

TS-S17 for [MainWatcher#21](https://github.com/Actium-Group-Corporation/MainWatcher/issues/21)
(ADR-016), run on 2026-09-18 against `main-watcher-sandbox/sample-target`, watched by the private
replica `main-watcher-sandbox/main-watcher`. The replica ran MainWatcher at `0c78a74` for rounds 1
to 5 and at `71af383` for round 6; the public `main-watcher-sandbox/gate` repo was republished at
`a236e65` between rounds 2 and 3, for the gate fix round 2 found. The trigger worker ran the same
build throughout, scaled to zero only for the lease lapses rounds 2 to 5 needed.

The target's settings for the whole run: `lock_lease: 10`, `poll_interval: 1`, and
`slow_check_minutes: 10`, which makes the second required check `sandbox-slow-check` sleep ten
minutes on a merge group, standing in for a target's own slow CI. That is what lets a group sit in
the queue with its gate already passed.

All times UTC.

## What each round shows

| Round | Clause of TS-S17 | Result |
|---|---|---|
| 1 | A group queued before the lock, gate already passed, is removed when the lock opens | Passed |
| 2 | A group that merged before its re-run is reported | Passed, and it found the gate bug below |
| 3 | (b) A crash right after a lease renewal, with an older `queue_swept`, still sweeps | Passed |
| 4, 5 | A gate **still running** when the lock is enforced again is re-run once it completes | Passed |
| 6 | The sweep happens in the cycle that opened the lock | Passed, after the fix round 1 asked for |

## Round 1 — the group ADR-016 exists for

| Time | What happened |
|---|---|
| 18:16:51 | `main` broken on purpose: `failing_tests: ["Alpha"]` in `sandbox.json` (`d6369b6`) |
| 18:17:03 | [sample-target#44](https://github.com/main-watcher-sandbox/sample-target/pull/44), unlabelled, added to the merge queue; branch `gh-readonly-queue/main/pr-44-d6369b6` |
| 18:17:12 | Its [gate run 35379287877](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35379287877) started and **passed**: no lock existed yet. `sandbox-slow-check` then held the group until ~18:27 |
| 18:20:26 | Lock [sample-target#45](https://github.com/main-watcher-sandbox/sample-target/issues/45) opened, carrying `sweep_required=2026-09-18T18:20:25Z` **in the same write** |
| 18:21:08 | The worker: `started watch.yml because lock #45: its queue sweep for 2026-09-18T18:20:25Z is unfinished` |
| 18:22:15 | That cycle: `Lock #45: queue sweep for 2026-09-18T18:20:25Z over 1 queued group(s): re-ran 1 gate run(s) — #44 (gate run 35379287877); complete` |
| 18:22:38 | Attempt 2 of the same gate run **failed**: ``` `main` is locked by #45 … Only PRs labelled `fixes-main` can merge. Not labelled: #44 ``` |
| 18:22:55 | The queue branch was gone: GitHub removed the group 40 s after the re-run, and 4½ minutes before its slow check would have passed |

**This is A-7 confirmed through the watcher's own sweep and the `main-watcher` App**, rather than
through the hand-driven spike of MainWatcher#6: re-running a required check that had passed makes
it pending again, and a failed re-run removes the group.

**The cycle that opened the lock did not sweep it.** It reported the check at 18:20:26 and then
logged neither a lease renewal nor a sweep: `GET /issues?state=open&labels=main-broken`, read about
a second later, did not hold the issue yet. The obligation carried, the worker asked for the next
cycle and that one swept — but #44 was free to merge for two minutes rather than a few seconds.
Round 6 below is the same scenario after that was fixed.

## Round 2 — a merge before any re-run, and a gate that could not read its lease

The worker was scaled to zero at 18:24:15 and lock #45's lease ran out at 18:32:13, so the gate
failed open (`reason=lease-expired`) for everything queued after that.

| Time | What happened |
|---|---|
| 18:32:48 | [#46](https://github.com/main-watcher-sandbox/sample-target/pull/46), unlabelled, queued. Its own branch set `slow_check_minutes: 0`, so its group had nothing slow to wait for |
| 18:33:45 | #46 **merged** onto the red, locked `main`. Nothing had re-run its gate, and nothing could have: the merge came 53 s after the gate passed |
| 18:35:00 | [#47](https://github.com/main-watcher-sandbox/sample-target/pull/47), unlabelled, queued; [gate run 35381055957](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35381055957) passed, failing open, and its group waited |
| 18:37:09 | A cycle with `MW_SANDBOX_EXIT_AFTER=renew` renewed the lapsed lease and exited: `sweep_required=18:37:09` written beside the **older** `queue_swept=18:20:25` |
| 18:38:01 | The worker, restored, dispatched a cycle for the owed sweep |
| 18:39:14 | The sweep re-ran #47's gate |
| 18:39:31 | Reconciliation reported #46's merge, writing its row into the lock's body |
| 18:39:40 | Attempt 2 of #47's gate **passed**: `The lock is open but its lease is missing, unreadable, expired or more than 24 h ahead: #45 (no readable lease)` |
| 18:46:03 | #47 merged, and reconciliation reported it too |

The merge at 18:33:45 is TS-S17's "a group that merged before its re-run is reported" clause, and
both merges were reported on the lock and as a `watcher-infra` alert.

The re-run at 18:39:40 was not. The gate required the issue body to hold **exactly one**
`<!-- main-watcher … -->` marker, which stopped being true when reconciliation began writing a
"Merged while locked" row per report (#20), each ending in its own hidden key. Nine seconds after
the first such row was written, the gate stopped reading a lease from that lock and failed open for
the rest of its life — in the state where a lock matters most, since a merge has already got past
it. `LockLease.ReadLeaseUntil` now collects `lease_until` across every marker and still refuses two
of them, with the body reconciliation really produces as its regression case (`a236e65`, published
to the public gate repo before round 3).

## Round 3 — TS-S17 (b), with the gate reading the lease again

The worker was scaled to zero at 18:50:27; the lease ran out at 19:00:00.

| Time | What happened |
|---|---|
| 19:00:21 | [#48](https://github.com/main-watcher-sandbox/sample-target/pull/48), unlabelled, queued during the lapse; [gate run 35383489839](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35383489839) passed, failing open, at 19:01 |
| 19:02:48 | A cycle with `MW_SANDBOX_EXIT_AFTER=renew` renewed the lapsed lease and died: `sweep_required=19:02:48`, `queue_swept=18:37:09` — the older marker TS-S17 (b) asks for |
| 19:04:39 | The next cycle renewed an **unexpired** lease, and left `sweep_required` at 19:02:48: an ordinary renewal changes nothing about the sweep |
| 19:04:41 | `Lock #45: queue sweep for 2026-09-18T19:02:48Z over 2 queued group(s): re-ran 1 gate run(s) — #48 (gate run 35383489839); complete` |
| 19:05:02 | Attempt 2 **failed**: ``` `main` is locked by #45 … Not labelled: #48 ```, and the group was removed |

The second queued group in that sweep was #49, queued after the lease was valid again: its own
first gate run failed, so the sweep left it alone, which is the "did not pass" case of the rule.

## Rounds 4 and 5 — a gate still running when the lock is enforced

Round 4 aimed to catch a gate mid-run by timing alone and missed twice: a sandbox gate run takes
about 40 s end to end, and the cycle reached its renewal 80 to 110 s after dispatch, so the gate had
always finished. Both attempts still swept what they found — #50 at 19:16:16 and #51 at 19:28:16,
each re-run and then removed — but through the "already completed" branch, not the one under test.
So the target's gate workflow was given a `sandbox-linger` step of `sleep 150`
**after** the gate action — the run goes on for two and a half minutes with its decision already
made, which is what a gate still running when a lock opens looks like, without racing GitHub's own
timings. It was removed afterwards.

| Time | What happened |
|---|---|
| 19:38:36 | [#52](https://github.com/main-watcher-sandbox/sample-target/pull/52) queued during the lapse; its [gate run 35387108981](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35387108981) decided at ~19:39:10, while the lease was still expired, and then lingered |
| 19:39:27 | The cycle renewed the lapsed lease: `sweep_required=19:39:27` |
| 19:39:31 | `…re-ran 0 gate run(s); still owed because gate run 35387108981 for #52 is still running`, and **no `queue_swept` was written** |
| 19:41:53 | The next cycle said the same |
| 19:43:04 | The worker: `started watch.yml because lock #45: its queue sweep for 2026-09-18T19:39:27Z is unfinished` |
| 19:44:18 | The run had completed, so: `re-ran 1 gate run(s) — #52 (gate run 35387108981); complete` |
| 19:44:40 | Attempt 2 **failed** on the lock, and #52 was removed |

## The unlock comment

A green `main`, pushed at 19:45:20, closed lock #45 at about 19:50, and its closing comment named every group the gate had
removed while it was open, marking the ones the sweep had re-run:

```
- #44 (its gate was re-run because it was queued before this lock)
- #48 (its gate was re-run because it was queued before this lock)
- #49
- #50 (its gate was re-run because it was queued before this lock)
- #51 (its gate was re-run because it was queued before this lock)
- #52 (its gate was re-run because it was queued before this lock)
```

#49 is the one the gate failed on its own first attempt, so it is listed plainly. Reading these at
all needs the gate-run list to reach back past the lock, because a re-run fails on an attempt
GitHub still dates the run by its first.

## Round 6 — the sweep in the lock's own cycle

With `71af383` on the replica, which hands the Reporter's freshly created locks to the sweep, round
1 was run again:

| Time | What happened |
|---|---|
| 20:03:07 | [#53](https://github.com/main-watcher-sandbox/sample-target/pull/53), unlabelled, queued while `main` was green; its [gate run 35389347411](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35389347411) passed |
| 20:06:21 | Lock [#54](https://github.com/main-watcher-sandbox/sample-target/issues/54) opened |
| 20:06:25 | The **same cycle**: `Lock #54: queue sweep for 2026-09-18T20:06:21Z over 1 queued group(s): re-ran 1 gate run(s) — #53 (gate run 35389347411); complete` |
| 20:07:00 | Attempt 2 failed and the group was gone — 39 s after the lock opened, against 2½ minutes in round 1 |

That cycle logged no lease renewal, for the same reason as round 1: the issue list still did not
hold the new lock. The sweep no longer depends on it.

## Restoration

`sample-target` is back to the seeded state: `sandbox.json` as the template has it, the gate
workflow pointing at `main-watcher-sandbox/gate@main` with no `sandbox-linger` step, no open locks,
no open pull requests and an empty queue. The scenario branches were deleted with their pull
requests. `MW_SANDBOX_EXIT_AFTER` was deleted after each round; the replica has no scenario
variables left, and the trigger worker is running.

`main` of `sample-target` carries the two merges rounds 2 made, which is what the scenario was
about, and the replica's `targets.yml` is the sandbox list.
