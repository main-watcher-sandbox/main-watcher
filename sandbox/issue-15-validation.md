---
owner: platform-team
reviewed: 2026-09-17
review_by: 2027-03-15
---

# Issue #15 sandbox validation

Validated on 2026-09-17 with worker implementation `d0ff858`, deployed to the cluster's
`main-watcher-sandbox` namespace from the locally built image `main-watcher-worker:dev`,
watching `main-watcher-sandbox/main-watcher` (the private watcher replica) and its one
configured target, `main-watcher-sandbox/sample-target` with `poll_interval: 1`. Every
`watch.yml` run below was started by the worker; "by hand" means the `pat-actium` account.

The PR #45 review found two ways the alerts could fire later than they should, fixed after
this run. Both only widen when an alert is raised — a report is now judged on every cycle
rather than only on one that looked at the target, and the "no run completed" clock takes the
run's own finishing time — so neither changes the path recorded below, where every cycle
looked at the target and the job's `completed_at` was known. The two cases they do change are
covered by unit tests, each of which fails against the code as it was.


## TS-S14 (c): with issue writes failing for 20 min the alert is raised, and the lock appears once writes succeed

**The fault.** The replica's `watch.yml` asks `actions/create-github-app-token` for
`permission-issues: write`. Commit
[`1b8e915`](https://github.com/main-watcher-sandbox/main-watcher/commit/1b8e9156c122198438c2ace7137acf4cd8e3b689)
narrowed that to `read` at 19:57:46Z, so every issue write the Reporter makes on the target
answers 403. Nothing else was changed: the check-run and Actions permissions stayed as they
are, and the `watcher-infra` alerts, which use the replica's own `GITHUB_TOKEN`, were
unaffected.

| Step | Evidence |
| --- | --- |
| Worker started, both Apps authenticated | 19:57:22Z, 19:57:23Z |
| Push, 19:57:58Z: `failing_tests: ["Alpha"]` | [`7d0edfa`](https://github.com/main-watcher-sandbox/sample-target/commit/7d0edfacda57296639a2c76f646340561a3be8b7) |
| Worker, 28 s later | `started watch.yml because head 7d0edfa is eligible for a test` → [35268016395](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35268016395) |
| Planner | check 105360203868 `in_progress` 19:59:11Z, `external_id` 35268091746 |
| Target run | `main-watcher-tests / main-watcher` `failure`, `completed_at` **20:00:06Z** |
| Worker, 19 s later | `started watch.yml because check 105360203868: the main-watcher job of target run 35268091746 has completed` |
| Reporter, every cycle | `Check 105360203868: Response status code does not indicate success: 403 (Forbidden).`, exit code 1; the check run stays `in_progress` |
| Failing `watch.yml` runs | 17, from [35268214319](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35268214319) at 20:00:26Z to [35270178901](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35270178901) at 20:20:09Z |
| **Alert raised, 20:15:27Z** | [main-watcher#9](https://github.com/main-watcher-sandbox/main-watcher/issues/9) "Reporting pending on main-watcher-sandbox/sample-target", `watcher-infra`, authored by `mw-doorbell[bot]` — 15 minutes after the job completed, not after the check run started |
| Worker log | `warn: MainWatcher.Worker.WorkerAlerts[0] Alert raised: Reporting pending on main-watcher-sandbox/sample-target.` |
| Issues permission restored, 20:21:02Z | [`262063b`](https://github.com/main-watcher-sandbox/main-watcher/commit/262063b25b80b5f9c9e034cbfd475150992cc37d) |
| **Lock written, 20:21:55Z** | [sample-target#27](https://github.com/main-watcher-sandbox/sample-target/issues/27), by the next cycle, [35270282480](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35270282480) |
| Check run completed, 20:21:56Z | 105360203868 `failure`, "Tests failed" — after the issue write, as ADR-013 point 2 requires |
| Alert condition cleared, 20:22:08Z | `info: MainWatcher.Worker.WorkerAlerts[0] Alert condition cleared: Reporting pending on main-watcher-sandbox/sample-target.` |

The lock is a normal one: it lists the failing `Alpha` test, links target run 35268091746,
names `0065d2f` as the last green commit with the one push since, and its marker reads
`reported_check=105360203868 reported_sha=7d0edfa…`. **No duplicate lock and no duplicate
comment** came out of the seventeen failed attempts, because each replay re-read the markers
first (ADR-013 point 4). The head was never retested while the report was owed: `7d0edfa`
has exactly one `main-watcher` check run.

## TS-U7 in the sandbox: a repeated condition comments on the open alert

The worker was restarted at 20:18:05Z, onto the same build this branch ships, while the
condition still held. Its in-memory record of what is firing starts empty, so it judged the
condition afresh — and `Alerts` found the open issue with the same title:

| Step | Evidence |
| --- | --- |
| Restart, condition still holding | 20:18:05Z |
| One comment on the open alert, 20:18:25Z | `mw-doorbell[bot]` on [main-watcher#9](https://github.com/main-watcher-sandbox/main-watcher/issues/9): "A report has been owed on `main-watcher-sandbox/sample-target` since 2026-09-17 20:00 UTC (18 minutes): check 105360203868: the main-watcher job of target run 35268091746 has completed." |
| No second issue | The only open `watcher-infra` issues were #9 and the pre-existing #2 |
| Nothing at 20:19Z, 20:20Z, 20:21Z | The repeat interval (1 h) suppressed the condition while it went on holding |

The issue opened at 20:15:27Z was written by an earlier build, whose opening clause named
the check run twice; the 20:18:25Z comment above is the wording this branch ships. Nothing
else about the condition or its de-duplication changed between the two builds.

## Restoration

| Step | Evidence |
| --- | --- |
| `permission-issues: write` restored | [`262063b`](https://github.com/main-watcher-sandbox/main-watcher/commit/262063b25b80b5f9c9e034cbfd475150992cc37d), 20:21:02Z |
| `sandbox.json` restored to green, 20:22:33Z | [`e643e8b`](https://github.com/main-watcher-sandbox/sample-target/commit/e643e8b11ec245f5aa3bd2964c254eb2c99db9b3) |
| Green run closed the lock | check 105368457084 `success`, "Tests passed"; sample-target#27 closed 20:25:58Z |
| Alert #9 closed by hand | It exists only because of the fault injection |
| Worker scaled to 0 | Its Deployment, ConfigMap and key Secret stay in the namespace |

The target ends green at `e643e8b` with no open issues; the replica's only open
`watcher-infra` issue is the pre-existing
[main-watcher#2](https://github.com/main-watcher-sandbox/main-watcher/issues/2) ("mention
nobody"), as before the run.

## Not exercised here

The four other alert conditions — three failing cycles in a row, no `watch.yml` run
completed in 2 h, a refused App credential, and a rate-limit budget below 20% — are covered
by `tests/MainWatcher.Worker.Tests/AlertTests.cs`, because each needs GitHub to fail or a
budget to be spent in a way the sandbox cannot produce on demand. The rate-limit reader is
tested against real GitHub header shapes there too. The hourly sweep's "trigger worker
appears down" alert is #16, so TS-S11 is still untouched.
