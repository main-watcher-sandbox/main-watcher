---
id: TS-001
type: test-strategy
status: draft
state: target
owner: platform-team
reviewed: 2026-09-21
review_by: 2027-03-15
sources: [ARCH-001, ADR-002, ADR-003, ADR-004, ADR-007, ADR-008, ADR-009, ADR-010, ADR-011, ADR-012, ADR-013, ADR-014, ADR-015, ADR-016, ADR-017, ADR-018]
confidence: assumed
---

# Test strategy — Main Watcher

## 1. What this system must be trusted to do

1. **Never block merges wrongly, and never leave them blocked after recovery.** A bug here
   stops every onboarded team.
2. **Always open a lock when `main` really fails**, with the correct tests and push list.
3. **Keep credentials narrow.** The worker can only read and start `watch.yml`; target code
   never runs near the main App key.
4. **Keep working when GitHub's scheduler or the worker fails**, just more slowly.

## 2. Responsibility matrix

| Test type | Owner | Runs where | Gate | Notes |
|---|---|---|---|---|
| Unit: worker change detection | Platform team | Worker repo CI (`dotnet test`, xUnit v3) | Merge | `GitHubGateway` faked |
| Unit: shared library (eligibility rule, `GitHubGateway`) | Platform team | CI of the library shared by the worker and the watcher scripts (`dotnet test`, xUnit v3) | Merge | Timestamped fixtures |
| Unit: Planner, Reporter (including replay), CTRF reader, push-range logic, gate decision (including lease), reconciliation (including closed locks) | Platform team | Watcher repo CI (`dotnet test`, xUnit v3) | Merge | GitHub API fixtures |
| Container smoke test | Platform team | Worker CI | Image publish | Starts the image; `/healthz` responds; config errors fail fast |
| Scenario TS-S1–S18 (`sandbox/run-scenarios.sh`) | Platform team | Sandbox org + the `main-watcher-sandbox` namespace in the cluster | Release of the worker, a new workflow tag, or a new gate template; TS-S13 alone on each reporter pin update | Real GitHub, one target per running scenario. About 1 h 45 min, measured (MainWatcher#25): TS-S16 (h)'s unstoppable run alone needs about 90 min of GitHub time, from its run deadline through the force-cancel to the alert, so the 30 min once planned cannot be reached |
| Workflow security review | Platform team + security | PR review | Any change to `.github/workflows` or App permissions | Checklist in §6 |
| Onboarding dry run | Target owner | Target repo | Before the gate becomes required | Onboarding step 5 |

## 3. Environments and test data

| Environment | Purpose | Production-like? | Data source |
|---|---|---|---|
| Sandbox org `main-watcher-sandbox` | Scenario tests | Yes: same App manifests, a real merge queue, a worker deployment in the `main-watcher-sandbox` namespace | Synthetic xUnit v3 repos seeded from `sandbox/sample-target`: the scenario suite's pool, `sample-target` and `sample-target-2` to `-10`, one target per running scenario; their tests pass, fail, hang or run for known durations according to `sandbox.json` |

## 4. Simulating dependencies

| Dependency | Stood in by | Drift risk | Real-dependency check |
|---|---|---|---|
| GitHub REST API | Fakes (worker) and recorded fixtures (watcher) | Payload shape changes: activity API, `merge_group`, dispatch `return_run_details` | Scenario suite before each release |
| Merge queue behaviour | Only the real thing | High | TS-S4, TS-S5, TS-S17 |
| `ctrf-io/github-test-reporter` | Nothing; only the real action, pinned | Medium (upstream changes) | TS-S13 on every pin update: `ci.yml`'s `reporter-pin` job fails a pull request that changes the pin until the `scenario-suite/ts-s13` status has passed on it |

## 5. Quality gates

| Gate | Must pass | Who may override | Override recorded where |
|---|---|---|---|
| Watcher repo or worker merge | Unit tests, workflow lint | Platform lead | PR comment |
| New workflow tag, gate template or worker image | TS-S1–S18: the `scenario-suite` status, which a full passing run of `sandbox/run-scenarios.sh` posts, and which `release.yml` requires on the commit before it moves the tag or pushes the image (`docs/release.md`) | No one | — |

## 6. Testing the architecture itself

| Claim | Requirement | Test | Frequency |
|---|---|---|---|
| TS-S1: an unchanged head causes no `watch.yml` run and no test run | FR-2, ADR-010 | Leave the sandbox idle for 10 min | Release |
| TS-S2: three quick pushes during a running test lead to exactly one further test, of the newest commit; the issue lists all pushes | FR-2, FR-3, ADR-009 | Slow suite; push 3 failing commits | Release |
| TS-S3: a green run closes the lock; an override lifts the gate; a failure on a newer commit opens a new issue | ADR-004 | Scripted sequence | Release |
| TS-S4: while locked, an unlabelled PR is removed from the queue and a `fixes-main` PR merges | FR-4, C-2 | Queue both | Release |
| TS-S5: a batched group mixing a fix and a non-fix PR fails the gate | R-3, A-5 | Merge limit 2 | Release, and at onboarding |
| TS-S6: a hand-made `main-broken` issue does not lock | R-2 | Issue created by a user | Release |
| TS-S7: with the worker scaled to 0 and the sweep disabled, merges still proceed: (a) with no lock open, at once; (b) starting from an open lock, once `lock_lease` has passed, with the gate warning "LOCK LEASE EXPIRED". After the watcher is restored, the lease is renewed, the lock is enforced again, a "lock lapsed" alert is raised, and the merge from (b) is reported | NFR-3, ADR-014 | Queue a PR in each case; sandbox `lock_lease` of 10 min | Release |
| TS-S8: credential scope. `mw-observer` cannot write or dispatch; `mw-doorbell` cannot touch targets; a target test run has no access to any Main Watcher key | ADR-009, ADR-010 | API calls with each token must return 403, with a working call beside each as control (`sandbox/ts-s8-credential-scope.sh`); the test run inspects its own environment (`inspect_environment` switch) | Release |
| TS-S9: when the gate cannot reach the API, it fails open with a warning, and the next watcher run reports the unlabelled merge | NFR-3, NFR-4, ADR-008 | Invalid-token override in the gate, lock open, unlabelled PR | Release |
| TS-S10: the lock issue notifies the `notify` team, and falls back to CODEOWNERS | CQ-6, R-10 | Sandbox team member checks their notifications | Release |
| TS-S11: with the worker scaled to 0, a push is tested by the next hourly sweep and a "worker appears down" alert is raised | ADR-010, R-5 | Scale down, push, wait | Release |
| TS-S13: the job summary lists the slowest tests with correct units, compared to xUnit v3's own CTRF values; the check run shows suite time, the 5 slowest tests and the retry flag | FR-6, ADR-011, ADR-018, R-17 | Sandbox suite with tests of known duration (e.g. 50 ms, 2 s, 20 s) | Release, and on each reporter pin update |
| TS-S12: cancelling a target test run marks its check run neutral, raises an alert, and retests the head after `poll_interval` | ADR-009, ADR-017 | Cancel the run by hand | Release |
| TS-S14: a Reporter stopped (a) after creating the lock issue and (b) after updating it, but before completing the check run, is replayed on the next cycle: the check run completes, no duplicate issue or comment appears, and the check run is never marked stale. (c) With issue writes failing for 20 min, a "reporting pending" alert is raised, and the lock appears once writes succeed. (d) Stopped after creating the lock issue, which a human then closes before the replay: no new lock is created, the check run completes as `failure`, and the override comment is posted | FR-4, ADR-004, ADR-013 | Fault-injection switch in the sandbox Reporter that exits after the chosen write; a read-only Issues token for (c) (`MW_SANDBOX_READ_ONLY_ISSUES`); for (d), close the issue by hand before restoring the Reporter | Release |
| TS-S15: an unlabelled PR merged during a lock, followed by a human closing the lock issue before the watcher recovers, is reported on recovery: on the closed issue, as a comment, and as a `watcher-infra` alert; the issue is then marked `reconciled=complete`. (b) The same, but with `fixes-main` added to the PR after it merged and before recovery: still reported | NFR-4, ADR-015 | Worker scaled to 0 and sweep disabled; gate forced open (invalid token or expired lease); merge, close the issue, restore; for (b), label the PR before restoring | Release |
| TS-S16: a failing test run whose CTRF upload fails opens a lock saying "failing tests unknown", not a neutral result; a run whose restore step fails completes the check run as `neutral`, with an alert and no lock; (c) the Reporter finds the `main-watcher-test` step in the real jobs response of a reusable-workflow caller, and a workflow tag with that step renamed produces an "outcome contract broken" alert; (d) a failing test run whose upload step then hangs past its timeout, and a failing test run cancelled by hand after the test step finished, both still open a lock, while a run cancelled during the test step itself is `neutral`; (e) a test that hangs past the target's timeout gives `neutral` with an infrastructure alert, not a lock, both without and with a `timeout-minutes` added to the test step, and `main-watcher-tests-finished` is `skipped` in both cases; (f) a failing test run whose separate `report` job stays waiting for a runner beyond the stale threshold still opens a lock as soon as the `main-watcher` job completes, and is never marked stale; (g) with a sandbox queue deadline of 10 min, a job that starts after 8 min and fails after its check run is 20 min old still opens a lock, a job that never gets a runner is cancelled after 10 min and gives `neutral`, and no second test starts before the cancelled run has stopped; (h) a failing test whose marker step succeeds, followed by a job that never completes, is cancelled at its run deadline and then opens a lock; with cancel and force-cancel made to fail, the check run stays `in_progress`, a "target run could not be stopped" alert is raised, and no test starts for a newer head until the run is deleted | FR-3, FR-4, ADR-007, ADR-013 | Sandbox switches that make the upload step fail, and that point restore at an unreachable feed; a sandbox workflow tag with the step renamed for (c); an upload step that sleeps past its timeout, and manual cancels at chosen points, for (d); a test that sleeps past a 2-min target timeout, with and without a step `timeout-minutes`, for (e); a sandbox switch that sends the `report` job to a runner label no runner has, for (f); a test job held in a concurrency group for 9 min (the `ts-s16-held-job` variant), and a runner label no runner has, for (g); an upload step with no timeout that never ends on the `ts-s16-long-timeout` variant, and a sandbox Planner whose cancel calls are made to fail, for (h) | Release |
| TS-S17: an unlabelled PR whose gate has passed while a slow second required check is still running is removed from the queue when a lock opens: the watcher re-runs its gate, which fails. The same holds for a gate still running when the lock opens. A group that merged before its re-run is reported. This confirms A-7. (b) A group passes the gate during a lease lapse; the Planner renews the lease and is stopped before sweeping, with an older `queue_swept` marker present: the next watcher run still re-runs that group's gate | FR-4, A-7, ADR-014, ADR-016 | Sandbox ruleset with a second required check that sleeps 10 min; force a failing test run while the PR waits; for (b), a 10-min `lock_lease` and a fault-injection exit after the renewal | Release, and before rollout |
| TS-S18: with `main` unchanged, a run whose restore step fails is retested after `poll_interval` and, once the feed is back, gives a real result; with the Planner stopped right after completing the check run as `neutral`, the head is still retested; after three neutral results a "head untestable" alert is raised and no fourth test starts until a push or a forced dispatch | FR-2, ADR-017 | Sandbox switch that points restore at an unreachable feed; fault-injection exit after the neutral write; sandbox `poll_interval` of 2 min | Release |
| TS-U1: the CTRF reader merges several projects' reports, lists failed tests, and treats missing or schema-invalid files as "unknown" | ADR-007 | xUnit v3-produced fixtures | Every commit |
| TS-U2: the reusable workflow's retry passes failing names to `--filter-method` and records the retry count | ADR-007, CQ-4 | Fixture reports | Every commit |
| TS-U3: no new test starts while a check run is in progress, or within `poll_interval` of the last start | CQ-1, FR-2 | Timestamped fixtures | Every commit |
| TS-U4: reconciliation reports each unlabelled merge during a lock exactly once across runs | ADR-008 | Activity fixtures + marker | Every commit |
| TS-U6: `timings.json` separates wall-clock time from summed per-test time, and carries the retry flag | ADR-011 | Fixture CTRF with parallel tests and a retry | Every commit |
| TS-U7: worker alerts de-duplicate: a repeated condition comments on the open issue instead of creating a new one | ADR-012 | Faked gateway | Every commit |
| TS-U5: the worker flags work for (a) an eligible head: new, or with a retryable neutral result (ADR-017), (b) a completed `main-watcher` job, whatever other jobs in the run are doing, (c) a run past its queue deadline (not started 30 min after the check run) or run deadline (still running its `timeout-minutes` + 10 min after it started), (d) a lock lease last renewed more than 1 h ago, (e) a closed lock not yet reconciled, and nothing otherwise; it dispatches at most once per cycle; a completed `main-watcher` job is never treated as stale, with or without an artifact; an unfinished job past its queue or run deadline is flagged whatever its `main-watcher-tests-finished` step shows, and stays flagged until the run has stopped | ADR-010, ADR-013, ADR-014, ADR-015 | Faked gateway | Every commit |
| TS-U8: Reporter replay is idempotent: stopping after any write in the create, update or close sequence and replaying for the same check run gives one issue, one comment and a completed check run; two open locks lead to the newer being closed; a check run ID already on a closed issue creates nothing; a red result for the commit of a human-closed lock creates nothing, while one for a different commit opens a new lock | ADR-004, ADR-013 | Faked gateway that fails after each write | Every commit |
| TS-U9: the gate enforces a lock only when `lease_until` is in the future and at most 24 h ahead; a missing, unreadable, expired or too-distant lease fails open with the "LOCK LEASE EXPIRED" warning | ADR-014 | Marker fixtures, fixed clock | Every commit |
| TS-U10: reconciliation covers merges up to a lock's `closed_at` for App-closed and human-closed issues, ignores merges after closure, writes `reconciled=complete` only after every report succeeds, and skips issues already complete; judges `fixes-main` from label events up to `merged_at`, so a label added after the merge still reports and one removed after it does not; stops without advancing `last_reconciled` when label events cannot be read | ADR-015 | Activity and issue fixtures | Every commit |
| TS-U11: the Reporter's outcome table, where the test step counts only after a successful `main-watcher-tests-finished` marker: marker succeeded and `main-watcher-test` failed → red, even if the run was then cancelled or timed out during upload; marker succeeded and the test step succeeded → green, even if the run was then cancelled; marker skipped, cancelled or missing → infrastructure error, including a test step reported as `failure` after a timeout; marker succeeded with the test step missing or in any other state → contract error; `main-watcher-test` failed with valid CTRF → red, tests listed; failed with missing, invalid or undownloadable CTRF → red, "failing tests unknown"; succeeded → green; run deleted (404) → neutral, "outcome unknown"; any other jobs API error → still pending; either step found more than once → contract error, neutral | ADR-007, ADR-013 | Jobs API and artifact fixtures, including a reusable-workflow caller's jobs response | Every commit |
| TS-U12: the queue sweep re-runs completed gate runs that started before the lock for groups still queued, re-runs gate runs still in progress once they complete, skips gate runs started after the lock and groups no longer queued, and writes `queue_swept` only when nothing is left; treats a sweep as owed whenever `queue_swept` is missing or earlier than `sweep_required`, including after a crash right after a lease renewal that left an older `queue_swept`; covers gate runs started up to 5 min after `sweep_required` | ADR-014, ADR-016 | Branch, workflow-run and issue fixtures | Every commit |
| TS-U13: the eligibility rule, implemented once in the shared .NET library: no check run → eligible once `poll_interval` has passed since the last start; newest `neutral` → eligible once `poll_interval` has passed since it completed, while the head has fewer than 3 neutral check runs; newest `in_progress`, `success` or `failure` → not eligible; the Planner re-checks before starting; `force` ignores only the cap; the worker and the Planner both call the library's rule and keep no copy of their own | ADR-017 | Timestamped check-run fixtures in the shared library's test suite | Every commit |
| TS-U14: the test wrapper sets `finished=true` only when the test command exits on its own, passes the command's exit code through after the one retry, and at its deadline stops the whole process tree and exits non-zero without setting `finished` | ADR-007, ADR-013 | Fake test commands that pass, fail, spawn child processes, and sleep past a short deadline | Every commit |
| TS-U15: the stale-run lifecycle: the queue deadline counts from check-run creation until the job starts, and the run deadline from the job's `started_at`; past either, the Planner records `cancel_requested`, cancels, and keeps the check run `in_progress` until the run stops; it force-cancels 15 min later; once the job has stopped, the outcome table is applied to its steps; 15 min after an unsuccessful force-cancel it raises an alert, keeps the check run `in_progress`, repeats the force-cancel each cycle, and starts no test for the target (no retry, newer head or forced dispatch) until the run stops or is deleted; a 404 gives "outcome unknown"; a jobs API error changes nothing; a crash between any two steps resumes from the recorded times | ADR-013, ADR-017 | Timestamped job, run and check-run fixtures; faked cancel and force-cancel responses | Every commit |

**Workflow and deployment review checklist:** each item is also a test in `SecurityChecklistTests` (watcher CI), except the App installations, which `sandbox/ts-s8-credential-scope.sh` checks against the live Apps.
- no `pull_request_target`;
- the reusable workflow requests no App tokens;
- `watch.yml` never checks out or runs target code;
- third-party actions pinned by commit SHA;
- the worker's Kubernetes Secret has restricted RBAC;
- `main-watcher` and `mw-observer` are installed on selected repositories only, matching
  `targets.yml` (plus the watcher repo for `mw-observer`; R-11);
- no inbound Service or Ingress exists for the worker;
- the `report` job references no secrets and requests only `actions: read` and
  `contents: read`.

## 7. Defect to regression

Any incident where merges were wrongly blocked, a real failure went unreported, or
detection stalled becomes a scenario or unit test. Owner: platform lead.

## 8. Traceability

| Requirement | Covered by |
|---|---|
| FR-2 | TS-S1, TS-S2, TS-S18, TS-U3, TS-U5, TS-U13 |
| FR-3 | TS-S2, TS-S16, TS-U1, TS-U11, TS-U14, TS-U15 |
| FR-4 | TS-S3, TS-S4, TS-S5, TS-S14, TS-S16, TS-S17, TS-U8, TS-U12 |
| FR-5 | TS-S8 |
| C-2 | TS-S4 |
| C-7 | TS-S11 |
| NFR-3 | TS-S7, TS-S9, TS-U9 |
| NFR-4 | TS-S9, TS-S15, TS-U4, TS-U10 |
| FR-6 | TS-S13, TS-U6 |
