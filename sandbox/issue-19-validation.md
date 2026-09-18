---
owner: platform-team
reviewed: 2026-09-18
review_by: 2027-03-15
---

# Issue #19 sandbox validation

TS-S7, run on 2026-09-18 with the lease work pushed to `main-watcher-sandbox/main-watcher`
(the private watcher replica). The target is `main-watcher-sandbox/sample-target` with
`poll_interval: 1` and, in the replica's `targets.yml`, **`lock_lease: 10`**, so a lease runs
out in ten minutes instead of four hours.

"The watcher is down" here means both of the things that start a `watch.yml` cycle: the trigger
worker scaled to zero in the `main-watcher-sandbox` namespace, and `watch.yml` itself disabled
in the replica, which stops the hourly sweep as well. Nothing else was changed: no fault switch,
no hand-edited marker, and every lock below is one the App opened from a real failing test run.

## What TS-S7 asks for

> With the worker scaled to 0 and the sweep disabled, merges still proceed: (a) with no lock
> open, at once; (b) starting from an open lock, once `lock_lease` has passed, with the gate
> warning "LOCK LEASE EXPIRED". After the watcher is restored, the lease is renewed, the lock is
> enforced again, a "lock lapsed" alert is raised, and the merge from (b) is reported.

Everything but the last clause is below. Reporting the merge made during the lapse is
reconciliation (ADR-015), which is #20 and does not exist yet; the lapse comment and the alert
both say so, and the merge is on the record here for that ticket to pick up.

## Setting up

| Time (UTC) | Event |
| --- | --- |
| 13:27 | Replica moved to [`9b25290`](https://github.com/main-watcher-sandbox/main-watcher/commit/9b2529018d54f40f42ce2fbcfe57c16cf66c9eec), whose tree is MainWatcher `bd0411f`, then `targets.yml` set to `lock_lease: 10` with the one target at `poll_interval: 1` |
| 13:29 | Worker image rebuilt from the same tree and rolled out; `mw-observer` and `mw-doorbell` authenticated, cycles clean. Its `targets.yml` read parses the new setting: the replica's own CI run shows `Target { … LockLease = 00:10:00 … }` (that run fails on `CommittedTargetListParses`, which asserts the committed list watches no sandbox target — true in MainWatcher, deliberately false in the replica) |
| 13:32:2x | `kubectl scale deploy/trigger-worker --replicas=0` |
| 13:32:41 | `gh workflow disable watch.yml` in the replica: the watcher is now down, sweep included |

## (a) With no lock open, an unlabelled PR merges at once

| Time (UTC) | Event |
| --- | --- |
| 13:33:27 | PR [sample-target#33](https://github.com/main-watcher-sandbox/sample-target/pull/33), unlabelled, added to the merge queue (`enqueuePullRequest`) |
| 13:34:03 | Merge-group gate [run 35350937777](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35350937777): `##[notice]No open main-broken issue authored by main-watcher[bot].` → `success` |
| 13:34:17 | The group merged as [`46d8779`](https://github.com/main-watcher-sandbox/sample-target/commit/46d8779c438cd8bbd62d6c7c06dfa716a770fc47) |

No watcher ran at any point in this: the gate needs only the target's own `GITHUB_TOKEN`, so a
watcher outage with no lock open costs nothing (NFR-3).

## A real lock, with a ten-minute lease

The watcher was restored to open a lock the ordinary way, from a failing test run rather than by
hand.

| Time (UTC) | Event |
| --- | --- |
| 13:35:03 | `sandbox.json` set to `failing_tests: ["Alpha"]` → head [`c0779d2`](https://github.com/main-watcher-sandbox/sample-target/commit/c0779d201b8db9a025d183acb12059cce25708c6) |
| 13:35:09 | Watcher restored: `watch.yml` enabled, worker scaled to 1 |
| 13:35:45 | Worker: `started watch.yml because head c0779d2 is eligible for a test` → cycle [35351131934](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35351131934): `Started check 105619657276, target run 35351256741.` |
| 13:39:21 | Cycle [35351371942](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35351371942): `Reported check 105619657276.` → check `failure`, lock [sample-target#34](https://github.com/main-watcher-sandbox/sample-target/issues/34) |

The lock's marker carries the lease the replica's `lock_lease` asks for — ten minutes, not four
hours:

```
<!-- main-watcher last_green=2c51f4c… first_red=c0779d2… lease_until=2026-09-18T13:49:20Z
     reported_check=105619657276 reported_sha=c0779d2… -->
```

That cycle logged no renewal line, and the issue's `updated_at` stayed at its `created_at`: the
`GET /issues?state=open&labels=main-broken` that `Renew` makes seconds after the lock is created
did not list it yet. Nothing is lost — the Reporter had just written a full-length lease — and
every later cycle renews it, as below. It is worth knowing that GitHub's issue list can lag its
own writes by a few seconds.

## The lock is enforced while its lease is valid

| Time (UTC) | Event |
| --- | --- |
| 13:40:40 | PR [sample-target#35](https://github.com/main-watcher-sandbox/sample-target/pull/35), unlabelled, added to the merge queue |
| 13:41:16 | Merge-group gate [run 35351641677](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35351641677): ``##[error]`main` is locked by #34 (…/issues/34). Only PRs labelled `fixes-main` can merge. Not labelled: #35 TS-S7 (b): unlabelled PR, lease valid.`` → `failure`, `main-watcher/gate-fail-open` `skipped` |
| 13:41:2x | The group was dropped and PR #35 left the queue, still open |
| 13:41:5x | `kubectl scale deploy/trigger-worker --replicas=0` |
| 13:42:01 | `gh workflow disable watch.yml`: the watcher is down again, with a lock open and 7 minutes of lease left |
