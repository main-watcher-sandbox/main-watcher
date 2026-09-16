---
owner: platform-team
reviewed: 2026-09-16
review_by: 2027-03-15
---

# Issue #11 sandbox validation

Validated on 2026-09-16 with watcher implementation `fba9094`, deployed to the
private `main-watcher-sandbox/main-watcher` replica, against
`main-watcher-sandbox/sample-target`. The last green commit before the scenario was
`fd25d49` (check 104966161964, from the #10 validation). Cycles were dispatched by hand,
standing in for the trigger worker.

## TS-S2: three quick pushes during a slow run

| Step | Evidence |
| --- | --- |
| Push 1, 21:11:32Z: `failing_tests: ["Alpha"]`, `slow_suite_minutes: 4` | `be16d65f4151f3cf3d98deafc144794bda3b5d31` |
| Planner | [35150976510](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35150976510): check 104979074917, target run [35151027799](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35151027799) |
| Push 2, 21:12:26Z: `["Alpha", "Beta"]` | `b3460f8f19ecbaf77fb3e354d8c0ebbe60154d32` |
| Push 3, 21:12:28Z: `["Beta"]` | `a88c425f49a09ab5b075e392b7d42945370f4f0f` |
| Cycle during the run | [35151066888](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35151066888): "Check 104979074917 remains pending", "No eligible head" |
| Run 1 | `main-watcher` job `failure` at 21:16:39Z |
| Reporter | [35151515601](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35151515601): reported check 104979074917, opened [sample-target#14](https://github.com/main-watcher-sandbox/sample-target/issues/14), then started check 104980934148 on `a88c425`, target run [35151587241](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35151587241) |
| Run 2 | `main-watcher` job `failure` (Beta) |
| Reporter | [35152050722](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35152050722): reported check 104980934148, "No eligible head" |

Lock #14 said "Since the last green commit, `fd25d49`" and listed all three pushes,
newest first, each as `pat-actium`, type `push`, with its before→after compare link and a
commit count of 1. Its marker began `last_green=fd25d49… first_red=be16d65…`.

Exactly one further test ran, of the newest commit `a88c425`; `b3460f8` has no
`main-watcher` check run. The second failure added one `main-watcher[bot]` comment naming
`a88c425`, the failing Beta test and the target run, ending in
`<!-- main-watcher check=104980934148 -->`, and mentioning nobody. No second issue opened.

The target has no `notify` list and no CODEOWNERS, so the lock raised the
"mention nobody" alert again, as
[main-watcher#2](https://github.com/main-watcher-sandbox/main-watcher/issues/2).

## Restoration

| Step | Evidence |
| --- | --- |
| Restore `sandbox.json` to `fd25d49`'s tree, 21:24Z | `65887a6eb73db133eabcbb4edc03a8fc5ca326ae` |
| Planner | [35152156455](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35152156455): check 104983038290, target run [35152221258](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35152221258), `success` |
| Reporter | [35152358936](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35152358936): lock #14 commented and closed at 21:27:18Z; check `success` at 21:27:19Z |

## Not exercised in the sandbox

The force-push, no-green and "push list unavailable" fallbacks, the merge types and the
unknown commit count are covered by unit tests in `MainWatcher.Core.Tests`, with the
activity and compare response shapes taken from `sample-target`'s real API responses.
