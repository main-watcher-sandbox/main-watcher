---
owner: platform-team
reviewed: 2026-09-17
review_by: 2027-03-15
---

# Issue #14 sandbox validation

Validated on 2026-09-17 with worker implementation `7518578`, deployed to the cluster's
`main-watcher-sandbox` namespace from the locally built image `main-watcher-worker:dev`,
watching `main-watcher-sandbox/main-watcher` (the private watcher replica, whose `main` was
the same commit) and its one configured target,
`main-watcher-sandbox/sample-target` with `poll_interval: 1`.

Unlike the #9 to #13 records, no cycle was dispatched by hand: every `watch.yml` run below
was started by the worker.

## Start-up

| Step | Evidence |
| --- | --- |
| Secret `trigger-worker-keys` mounted, deployment scaled to 1 at 18:23:29Z | `kubectl apply -k deploy/worker/sandbox` |
| Both Apps authenticated before the first cycle | `mw-observer authenticated to main-watcher-sandbox/main-watcher` 18:23:32Z; `mw-doorbell …` 18:23:33Z |
| `/healthz` through a port-forward | `{"live":true,"last_cycle":"2026-09-17T18:52:34Z","last_result":{"targets":1,"dispatched":0,"errors":0}}` |

Before the keys were installed, the same deployment with a throwaway key crash-looped with
`Configuration error: mw-observer could not authenticate to main-watcher-sandbox/main-watcher
(HTTP 401): check its App ID, its private key, and that the App is installed on that
repository.` — the credential check, `STATUS Error, RESTARTS 3`. The PR #44 review moved that
check inside the cycle loop, so `/healthz` now serves while it runs; the container smoke test
covers both halves.

## TS-S1: an unchanged head causes no `watch.yml` run and no test run

The head was `327ee4e`, already green from check 105306156191 (17:18:37Z), with no open
lock.

| Step | Evidence |
| --- | --- |
| Idle window | 18:23:50Z to 18:34:55Z, 11 minutes |
| Worker cycles in the window | 12, every one `Cycle finished: 1 targets, 0 dispatched, 0 errors` |
| `watch.yml` runs | None: newest was [35252036143](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35252036143) at 17:19:35Z, before the worker started |
| Target test runs | None: newest was [35251942609](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35251942609) at 17:18:39Z |
| Check runs on the head | Still the one, 105306156191 |

## TS-S2: three quick pushes during a running test

`timed_tests` was switched off for the scenario, so a run is the 4-minute `SlowSuite` plus
the failing test.

| Step | Evidence |
| --- | --- |
| Push 1, 18:35:24Z: `failing_tests: ["Alpha"]`, `slow_suite_minutes: 4` | `0b1f3486dca80ca72d221e96faf1618b14b1702d` |
| Worker, 12 s later | `started watch.yml because head 0b1f348 is eligible for a test` → [35259769905](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35259769905), run name `watch main-watcher-sandbox/sample-target` |
| Planner | check 105332571276 `in_progress` at 18:36:32Z, `external_id` 35259865289 |
| Push 2, 18:36:54Z: `["Alpha", "Beta"]` | `d64fdcc1ff73114aec37dfff1445e915c901293e` |
| Push 3, 18:36:56Z: `["Beta"]` | `54e9b8f7f7d032784de459ec4925603cfdd405f6` |
| Worker during the run | Cycles at 18:36:33Z, 18:37:35Z, 18:38:35Z, 18:39:35Z, 18:40:35Z: all `0 dispatched`, although two newer heads existed |
| Run 1 | `main-watcher-tests / main-watcher` `failure` at 18:41:12Z, while `main-watcher-tests / report` was still `in_progress` |
| Worker, 24 s later | `started watch.yml because check 105332571276: the main-watcher job of target run 35259865289 has completed` → [35260369122](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35260369122) |
| Reporter | `Reported check 105332571276`, `walked back 1 commits … (push list: SinceGreen)`, opened [sample-target#26](https://github.com/main-watcher-sandbox/sample-target/issues/26) at 18:42:32Z |
| Planner, same cycle | `Started check 105334601222, target run 35260470812` on `54e9b8f` |
| Run 2 | `main-watcher` job `failure` (Beta) |
| Worker | `started watch.yml because check 105334601222: the main-watcher job of target run 35260470812 has completed` at 18:47:35Z |
| Reporter | Commented on #26 at 18:48:20Z; check 105334601222 `failure`, "Tests failed" |

Lock #26 said "Since the last green commit, `327ee4e`" and listed all three pushes, newest
first, each as `pat-actium`, type `push`, with a compare link and a commit count of 1. Its
marker held `last_green=327ee4e… first_red=0b1f348… lease_until=2026-09-17T22:42:32Z
reported_check=105332571276 reported_sha=0b1f348…`.

**Exactly one further test ran**, of the newest commit `54e9b8f`: `d64fdcc` has no
`main-watcher` check run at all. The second failure added one `main-watcher[bot]` comment
naming `54e9b8f`, the failing Beta test and its target run, ending in
`<!-- main-watcher check=105334601222 sha=54e9b8f… -->`. No second lock was opened.

As in #11, the target has no `notify` list and no CODEOWNERS, so the lock commented on the
open "mention nobody" alert,
[main-watcher#2](https://github.com/main-watcher-sandbox/main-watcher/issues/2), rather than
opening a second one.

## Restoration

| Step | Evidence |
| --- | --- |
| Restore `sandbox.json` to `327ee4e`'s tree, 18:48:33Z | `0065d2f3c72434377761da1d1fc9fa92befb45e2` |
| Worker | `started watch.yml because head 0065d2f is eligible for a test` at 18:48:36Z; check 105336982058, target run [35261177148](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35261177148) |
| Worker | `started watch.yml because check 105336982058: the main-watcher job of target run 35261177148 has completed` at 18:51:36Z |
| Reporter | Commented on #26 and closed it at 18:52:28Z; check 105336982058 `success`, "Tests passed" |

The target ends green at `0065d2f` with no open issues. The worker was scaled back to 0
replicas after the run; its Deployment, ConfigMap and key Secret stay in the namespace.

## Not exercised here

The worker's stale-run, lease, reconciliation and queue-sweep triggers do not exist yet
(#18 to #21), nor its own alerts (#15) or the hourly sweep (#16), so TS-S7 and TS-S11 are
untouched. The unlinked-check rules (a lost dispatch response) and the deleted-run and
contract-error rows of the work table are covered by unit tests in
`MainWatcher.Worker.Tests`, since they need a lost HTTP response or a deleted run to
reproduce.
