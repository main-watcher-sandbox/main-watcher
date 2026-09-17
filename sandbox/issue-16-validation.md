---
owner: platform-team
reviewed: 2026-09-17
review_by: 2027-03-15
---

# Issue #16 sandbox validation

Validated on 2026-09-17 with the sweep implementation pushed to
`main-watcher-sandbox/main-watcher` (the private watcher replica) as
[`09d4b44`](https://github.com/main-watcher-sandbox/main-watcher/commit/09d4b44844a96b6ff439a6c477662c85dd147d0a),
whose tree is MainWatcher `982dbd3`. The target is `main-watcher-sandbox/sample-target` with
`poll_interval: 1`, and the trigger worker was scaled to zero throughout:

```
kubectl -n main-watcher-sandbox get deploy trigger-worker   # 0 replicas
```

"By hand" means the `pat-actium` account.

## Shape: a dispatch with no target sweeps every enabled target

TBD

## TS-S11: with the worker scaled to 0, a push is tested by the next sweep and the alert is raised

TBD
