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

## (b) Once `lock_lease` has passed, the gate fails open and the merge proceeds

The lease ran out at 13:49:20Z with the watcher still down. Nothing had touched the lock since
it opened: at 13:49 its `updated_at` was still `2026-09-18T13:39:21Z`, with no comments.

| Time (UTC) | Event |
| --- | --- |
| 13:50:19 | The same PR [sample-target#35](https://github.com/main-watcher-sandbox/sample-target/pull/35), still unlabelled, added to the merge queue again |
| 13:50:57 | Merge-group gate [run 35352599988](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35352599988): ``##[warning]The lock is open but its lease is missing, unreadable, expired or more than 24 h ahead: #34 (lease_until=2026-09-18T13:49:20Z). The watcher has st…`` → `main-watcher-gate` `success` |
| 13:51:02 | `main-watcher/gate-fail-open` ran and succeeded, rather than being skipped as it is on an enforced lock |
| 13:51:10 | The group merged as [`5e191b1`](https://github.com/main-watcher-sandbox/sample-target/commit/5e191b1a1aa3b3b4b59b2a601044fdb5177aeab5) — an unlabelled PR onto a `main` that is still red |

This is the NFR-3 trade ADR-014 chose, seen end to end: the lock stayed open and the issue was
never touched, but ten minutes without a watcher was enough for the gate to stop enforcing it.
The gate needed no new credential to decide that; it read the marker it already reads.

## The watcher returns: renewal, lapse and enforcement again

| Time (UTC) | Event |
| --- | --- |
| 13:52:18 | `watch.yml` enabled |
| 13:52:2x | Worker scaled back to 1; `mw-observer` and `mw-doorbell` authenticated at 13:52:24 |
| 13:52:54 | First cycle back, dispatched by the worker for the new head: `started watch.yml because head 5e191b1 is eligible for a test` → cycle [35352824473](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35352824473) |
| 13:53:56 | That cycle's renewal, logged before it planned anything: `Lock #34: lease renewed until 2026-09-18T14:03:56Z; it had lapsed at 2026-09-18T13:49:20Z.` |
| 13:53:57 | Lapse comment on lock #34 |
| 13:53:58 | Alert [main-watcher#21](https://github.com/main-watcher-sandbox/main-watcher/issues/21) "Lock lease lapsed on main-watcher-sandbox/sample-target" |
| 13:53:59 | `lapse_reported` written; the issue's `updated_at` settles here |
| 13:54:0x | The same cycle went on to `Started check 105625173615, target run 35352935761.` for the new head |

The marker after recovery, all of it written by that one cycle:

```
<!-- main-watcher last_green=2c51f4c… first_red=c0779d2… reported_check=105619657276
     reported_sha=c0779d2… lease_until=2026-09-18T14:03:56Z
     lapsed=2026-09-18T13:49:20Z..2026-09-18T13:53:56Z sweep_required=2026-09-18T13:53:56Z
     lapse_reported=2026-09-18T13:53:56Z -->
```

`lease_until`, `lapsed` and `sweep_required` share one timestamp and one write, which is what
ADR-014 point 4 asks for: a crash straight after the renewal cannot leave a lock that is
enforced again with no record that it ever stopped being. `lapse_reported` is a second write,
made only after the comment and the alert, and it carries the renewal time, so a cycle that dies
between them leaves the pair owed and a later one posts what is missing.

The comment, `main-watcher[bot]` on #34:

> This lock's lease ran out at 2026-09-18T13:49:20Z and was renewed at 2026-09-18T13:53:56Z, 5
> minutes later. While a lease is expired the gate fails open, so the merge queue accepted pull
> requests without the `fixes-main` label during that window (ADR-014). The lock is enforced
> again now.
>
> `<!-- main-watcher lapsed=2026-09-18T13:49:20Z..2026-09-18T13:53:56Z -->`

The merge queue did exactly that: `5e191b1` is the unlabelled PR #35. Reporting it against the
lock is reconciliation's job (#20); the alert says so, and both the comment and the alert carry
the lapse window as their de-duplication key.

Enforcement came straight back:

| Time (UTC) | Event |
| --- | --- |
| 13:55:45 | PR [sample-target#36](https://github.com/main-watcher-sandbox/sample-target/pull/36), unlabelled, added to the merge queue |
| 13:56:14 | Merge-group gate [run 35353120272](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35353120272): ``##[error]`main` is locked by #34 … Only PRs labelled `fixes-main` can merge.`` → `failure` |
