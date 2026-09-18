---
owner: platform-team
reviewed: 2026-09-17
review_by: 2027-03-15
---

# Issue #17 sandbox validation

Validated on 2026-09-17 (the UTC timestamps below fall just after midnight on the 18th)
with the retry work pushed to `main-watcher-sandbox/main-watcher` (the
private watcher replica) as
[`b8ab7f6`](https://github.com/main-watcher-sandbox/main-watcher/commit/b8ab7f6169c0f899aa1c576c6f75cc2ed842654e)
at 00:51Z, whose tree is MainWatcher `013d8eb`. The target is
`main-watcher-sandbox/sample-target` with `poll_interval: 1`.

Unlike the #16 run, the trigger worker was **left running** in the `main-watcher-sandbox`
namespace throughout, so most cycles below were dispatched by `mw-doorbell[bot]` on its own
60-second poll. That is deliberate: the worker and the Planner read ADR-017's rule from the same
`Eligibility` code, so a run with the worker alive exercises both callers of it at once. Cycles
dispatched by `pat-actium` are called "by hand" below; every other cycle is the worker's.

Every check-run listing here uses `?filter=all`, because a commit with several attempts has
several `main-watcher` check runs and GitHub's default `filter=latest` shows only the newest.

## TS-S12: a cancelled target run is neutral, is alerted, and the head is tested again

Run twice. The first cancel landed a moment too late and produced a different neutral kind,
which is recorded here because it is a real property of the ADR-013 table, not a mishap.

### First cancel, landing as the tests finished

Head [`8133a86`](https://github.com/main-watcher-sandbox/sample-target/commit/8133a860c915eb462be51fd66e18e0fc632c9184),
pushed 00:52:39Z.

| Time (UTC) | Event |
| --- | --- |
| 00:53:46 | Cycle by hand [35293026623](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35293026623): `Started check 105439914355, target run 35293085791` |
| 00:54:30 | Target run cancelled by hand, about 45 s after `main-watcher-test` began |
| — | The job's steps: `main-watcher-test: cancelled`, `main-watcher-tests-finished: success`. The tests had in fact finished and the runner had set `finished=true`; the cancel caught the step in teardown |
| 00:55:55 | Cycle by hand [35293169294](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35293169294): `Reported check 105439914355.` → `neutral`, output title **Outcome contract broken** (marker `success` with no test result), alert [main-watcher#12](https://github.com/main-watcher-sandbox/main-watcher/issues/12) |
| 00:57:52 | Cycle by hand [35293301818](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35293301818): `Started check 105440759214` — a **second check run on the same commit**, `main` unchanged |
| 00:59:57 | Cycle by hand [35293445160](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35293445160): check 105440759214 completes `success`, "Tests passed" |

The retest produced a real result with nothing about `main` changed between the two attempts,
which is the half of TS-S18 that the `fail_restore` switch cannot show: that switch lives in the
target's own `Directory.Build.props`, so making the feed reachable again is necessarily a push.

### Second cancel, landing inside the test step

Head [`2db864f`](https://github.com/main-watcher-sandbox/sample-target/commit/2db864f1bbc7e9bcd48999dd2877639cd53a0015),
pushed 01:00:31Z with `slow_suite_minutes: 3` so the test step would still be running.

| Time (UTC) | Event |
| --- | --- |
| 01:01:34 | Cycle by hand [35293557438](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35293557438): `Started check 105441519062, target run 35293619516` |
| 01:02:45 | Target run cancelled by hand, about 60 s into `main-watcher-test` |
| — | Steps: `main-watcher-test: cancelled`, `main-watcher-tests-finished: skipped` |
| 01:04:19 | Alert [main-watcher#13](https://github.com/main-watcher-sandbox/main-watcher/issues/13), "Infrastructure error on …" |
| 01:04:20 | Check 105441519062 completes `neutral`, output title **Infrastructure error** |
| 01:05:22 | `Started check 105442307393` — the retest of the same commit. `poll_interval` had already passed, so the cycle that reported the neutral started it in the same run |
| 01:10:22 | Check 105442307393 completes `success`, "Tests passed" |

At 01:03:25Z and 01:03:26Z the worker and a hand dispatch both started a cycle for this target,
one second apart. The per-target concurrency group serialised them and only one check run was
created: the duplicate-dispatch case of §5.1.

## TS-S18: three neutral results reach the cap, and the alert says so

Head [`4afc411`](https://github.com/main-watcher-sandbox/sample-target/commit/4afc4115314433074ebedf928a0bc55068fd85d5),
pushed 01:10:36Z with `fail_restore: true`, so every attempt fails `dotnet restore` with NU1301
and leaves the marker step skipped. Every cycle in this section is the worker's.

| Attempt | Check run | Started | Completed | Result |
| --- | --- | --- | --- | --- |
| 1 | 105443861030 | 01:12:57 | 01:15:33 | `neutral`, Infrastructure error |
| 2 | 105444954520 | 01:18:20 | 01:20:22 | `neutral`, Infrastructure error, plus [main-watcher#14](https://github.com/main-watcher-sandbox/main-watcher/issues/14), "Infrastructure errors twice in a row" |
| 3 | 105445759995 | 01:22:22 | 01:24:26 | `neutral`, Infrastructure error |

Each attempt waited out `poll_interval` after the previous one completed; nothing retried inside
the cycle that wrote a neutral.

**01:24:28Z — alert [main-watcher#15](https://github.com/main-watcher-sandbox/main-watcher/issues/15),
"Head untestable on main-watcher-sandbox/sample-target"**, two seconds after the third neutral,
raised by the same cycle ([35295117410](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35295117410)).
Its body named the commit, said "which has 3 `neutral` check runs and no result since", reported
"No lock is open, so a failure on this head would go unreported", named the two ways to resume,
and carried `<!-- main-watcher untestable sha=4afc411… -->`.

### No fourth test starts

| Time (UTC) | Event |
| --- | --- |
| 01:23:25 → 01:34:25 | The worker dispatched **no** `watch.yml` run for 11 minutes. It reads the same `Eligibility` rule, so it stopped flagging the head at the same moment the Planner did |
| 01:29 | The head still had exactly three `main-watcher` check runs, five minutes after the cap |
| 01:32:12 | Cycle by hand [35295644014](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35295644014) with `force: false`: `No eligible head.` Alert #15 gained no comment — the `untestable sha=` marker suppressed the repeat |

### A forced dispatch ignores the cap

| Time (UTC) | Event |
| --- | --- |
| 01:32:42 | `gh workflow run watch.yml -f target=… -f force=true` → [35295744888](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35295744888) |
| 01:33:38 | `Started check 105447979670, target run 35295811452` — the fourth test of the same head |
| ~01:36 | It failed restore too and completed `neutral`. Alert #15 still had no comment: the head was already named, so the cap was not announced twice |

## TS-S18: a cycle stopped at the neutral write still leads to a retest

### First attempt: cancelled a moment too late

Head [`4a0f6a5`](https://github.com/main-watcher-sandbox/sample-target/commit/4a0f6a590069352bb0ee659aae20970d5ab6d01d),
pushed 01:35:47Z with the feed still unreachable.

| Time (UTC) | Event |
| --- | --- |
| 01:37:21 | Check 105448732561 created, target run 35296071617 |
| 01:39:21 | Worker cycle [35296140599](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35296140599): `Reported check 105448732561.` — the check run completes `neutral` |
| 01:39:22 | The same cycle logs `No eligible head.`: `poll_interval` had not passed, so it planned nothing |
| 01:39:27 | The run was cancelled by hand. Its `Plan and report` step is `cancelled`, and the job ends `cancelled` |
| 01:41:22 | A later cycle starts check 105449529041 on the **same commit** |
| 01:43:17 | It completes `neutral` as well — the feed was still down, but the retest happened |

The retest happened, but this does not verify what TS-S18 asks for. The cancel landed five
seconds after the neutral write, and by then the cycle had already planned: `No eligible head.`
is the Planner running and declining. The crash between reporting and planning was not
exercised, which the PR #48 review called out. The run below does exercise it.

### Second attempt: the fault switch, at the boundary itself

`MW_SANDBOX_EXIT_AFTER` now names the check run's own completion (`check:neutral`), so the cycle
can be killed with the neutral written and nothing else run. Watcher replica at
[`f545883`](https://github.com/main-watcher-sandbox/main-watcher/commit/f545883), whose tree is
MainWatcher `8bcc6e0`; `MW_SANDBOX_EXIT_AFTER=check:neutral` set at 02:18:59Z. Head
[`761651b`](https://github.com/main-watcher-sandbox/sample-target/commit/761651b43ea44060b17ba36ef23ec3f9ab2aea6b),
pushed 02:19:11Z with `fail_restore: true`.

| Time (UTC) | Event |
| --- | --- |
| 02:20:29 | Check 105457305332 created, target run 35298958853 |
| 02:24:26 | Worker cycle [35299118941](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35299118941) completes the check run `neutral`, after its "Infrastructure error" alert |
| 02:24:27 | `MW_SANDBOX_EXIT_AFTER: exiting after the Reporter's check:neutral write.` → `Process completed with exit code 3`. The `Plan and report` step is `failure` and the job fails |
| — | The log has **no** `Reported check` line and **no** `No eligible head.` line: the process died inside `Report`, before it returned and before the Planner was reached |
| 02:27:19 | A later cycle starts check 105458610152 on the **same commit** |

Nothing was carried between the two cycles. The neutral write is the whole of the failure
handling, so there was no retry step for the kill to lose (ADR-017 point 2). The variable was
deleted at 02:25Z; only that one cycle failed, well under the worker's three-in-a-row threshold.

## TS-S18: with the feed back, the head gives a real result

Head [`86797cc`](https://github.com/main-watcher-sandbox/sample-target/commit/86797cc6957de46325d5d565f676c0da854cf2b8),
pushed 01:43:30Z with `fail_restore: false`. Check 105450316666 started 01:45:22 and completed
`success`, "Tests passed", at 01:47:17 — testing resumed on the new head with no forced dispatch,
which is the other way out of the cap.

## What this did not cover

- The `fail_restore` switch is a file in the target, so "the feed is back" is always a new head.
  The unchanged-`main` half of TS-S18's first clause is shown by TS-S12 instead, where a cancel
  and its retest happen on one commit.
- TS-S16 (g) and (h), the cancel and force-cancel of a stale run, stay with #18; nothing here
  cancelled a run the watcher was supposed to cancel itself.

## Restoring the sandbox

`sandbox.json` is back at its template values and the `MW_SANDBOX_EXIT_AFTER` variable is
deleted. The scratch files `ts-s12.md` and `ts-s18.md` were removed, and both the head that
removed them and
[`bcd550f`](https://github.com/main-watcher-sandbox/sample-target/commit/bcd550f756232d526c532f444f99d47d734404e9),
the head that put the feed back after the second TS-S18 attempt, tested green (check
105459041776, "Tests passed", 02:33Z). Alerts #12 to #17 were closed by hand; they are linked
above and stay readable.
