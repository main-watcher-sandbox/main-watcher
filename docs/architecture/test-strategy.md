---
id: TS-001
type: test-strategy
status: draft
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
sources: [ARCH-001, ADR-002, ADR-003, ADR-004, ADR-007, ADR-008, ADR-009, ADR-010, ADR-011, ADR-012]
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
| Unit: Planner, Reporter, CTRF reader, push-range logic, gate decision, reconciliation | Platform team | Watcher repo CI | Merge | GitHub API fixtures |
| Container smoke test | Platform team | Worker CI | Image publish | Starts the image; `/healthz` responds; config errors fail fast |
| Scenario TS-S1–S12 | Platform team | Sandbox org + a sandbox namespace in the cluster | Release of the worker, a new workflow tag, or a new gate template | Real GitHub, about 30 min |
| Workflow security review | Platform team + security | PR review | Any change to `.github/workflows` or App permissions | Checklist in §6 |
| Onboarding dry run | Target owner | Target repo | Before the gate becomes required | Onboarding step 5 |

## 3. Environments and test data

| Environment | Purpose | Production-like? | Data source |
|---|---|---|---|
| Sandbox org `main-watcher-sandbox` `[assumption]` | Scenario tests | Yes: same App manifests, a real merge queue, a worker deployment | Synthetic xUnit v3 repo whose tests pass or fail according to a file; a second synthetic repo with a "slow" suite |

## 4. Simulating dependencies

| Dependency | Stood in by | Drift risk | Real-dependency check |
|---|---|---|---|
| GitHub REST API | Fakes (worker) and recorded fixtures (watcher) | Payload shape changes: activity API, `merge_group`, dispatch `return_run_details` | Scenario suite before each release |
| Merge queue behaviour | Only the real thing | High | TS-S4, TS-S5 |
| `ctrf-io/github-test-reporter` | Nothing; only the real action, pinned | Medium (upstream changes) | TS-S13 on every pin update |

## 5. Quality gates

| Gate | Must pass | Who may override | Override recorded where |
|---|---|---|---|
| Watcher repo or worker merge | Unit tests, workflow lint | Platform lead | PR comment |
| New workflow tag, gate template or worker image | TS-S1–S12 | No one | — |

## 6. Testing the architecture itself

| Claim | Requirement | Test | Frequency |
|---|---|---|---|
| TS-S1: an unchanged head causes no `watch.yml` run and no test run | FR-2, ADR-010 | Leave the sandbox idle for 10 min | Release |
| TS-S2: three quick pushes during a running test lead to exactly one further test, of the newest commit; the issue lists all pushes | FR-2, FR-3, ADR-009 | Slow suite; push 3 failing commits | Release |
| TS-S3: a green run closes the lock; an override lifts the gate; a failure on a newer commit opens a new issue | ADR-004 | Scripted sequence | Release |
| TS-S4: while locked, an unlabelled PR is removed from the queue and a `fixes-main` PR merges | FR-4, C-2 | Queue both | Release |
| TS-S5: a batched group mixing a fix and a non-fix PR fails the gate | R-3, A-5 | Merge limit 2 | Release, and at onboarding |
| TS-S6: a hand-made `main-broken` issue does not lock | R-2 | Issue created by a user | Release |
| TS-S7: with the worker scaled to 0 and the sweep disabled, merges still proceed | NFR-3 | Queue a PR | Release |
| TS-S8: credential scope. `mw-observer` cannot write or dispatch; `mw-doorbell` cannot touch targets; a target test run has no access to any Main Watcher key | ADR-009, ADR-010 | API calls with each token must return 403; the test run inspects its own environment | Release |
| TS-S9: when the gate cannot reach the API, it fails open with a warning, and the next watcher run reports the unlabelled merge | NFR-3, NFR-4, ADR-008 | Invalid-token override in the gate, lock open, unlabelled PR | Release |
| TS-S10: the lock issue notifies the `notify` team, and falls back to CODEOWNERS | CQ-6, R-10 | Sandbox team member checks their notifications | Release |
| TS-S11: with the worker scaled to 0, a push is tested by the next hourly sweep and a "worker appears down" alert is raised | ADR-010, R-5 | Scale down, push, wait | Release |
| TS-S13: the job summary lists the slowest tests with correct units, compared to xUnit v3's own CTRF values; the check run shows suite time, the 5 slowest tests and the retry flag | FR-6, ADR-011, R-17 | Sandbox suite with tests of known duration (e.g. 50 ms, 2 s, 20 s) | Release, and on each reporter pin update |
| TS-S12: cancelling a target test run marks its check run neutral, raises an alert, and retests the head | ADR-009 | Cancel the run by hand | Release |
| TS-U1: the CTRF reader merges several projects' reports, lists failed tests, and treats missing or schema-invalid files as "unknown" | ADR-007 | xUnit v3-produced fixtures | Every commit |
| TS-U2: the reusable workflow's retry passes failing names to `--filter-method` and records the retry count | ADR-007, CQ-4 | Fixture reports | Every commit |
| TS-U3: no new test starts while a check run is in progress, or within `poll_interval` of the last start | CQ-1, FR-2 | Timestamped fixtures | Every commit |
| TS-U4: reconciliation reports each unlabelled merge during a lock exactly once across runs | ADR-008 | Activity fixtures + marker | Every commit |
| TS-U6: `timings.json` separates wall-clock time from summed per-test time, and carries the retry flag | ADR-011 | Fixture CTRF with parallel tests and a retry | Every commit |
| TS-U7: worker alerts de-duplicate: a repeated condition comments on the open issue instead of creating a new one | ADR-012 | Faked gateway | Every commit |
| TS-U5: the worker flags work for (a) a new head, (b) a completed target run, (c) a stale run, and nothing otherwise; it dispatches at most once per cycle | ADR-010 | Faked gateway | Every commit |

**Workflow and deployment review checklist:**
- no `pull_request_target`;
- the reusable workflow requests no App tokens;
- `watch.yml` never checks out or runs target code;
- third-party actions pinned by commit SHA;
- the worker's Kubernetes Secret has restricted RBAC;
- no inbound Service or Ingress exists for the worker;
- the `report` job references no secrets and requests only `actions: read` and
  `contents: read`.

## 7. Defect to regression

Any incident where merges were wrongly blocked, a real failure went unreported, or
detection stalled becomes a scenario or unit test. Owner: platform lead.

## 8. Traceability

| Requirement | Covered by |
|---|---|
| FR-2 | TS-S1, TS-S2, TS-U3, TS-U5 |
| FR-3 | TS-S2, TS-U1 |
| FR-4 | TS-S3, TS-S4, TS-S5 |
| FR-5 | TS-S8 |
| C-2 | TS-S4 |
| C-7 | TS-S11 |
| NFR-3 | TS-S7, TS-S9 |
| NFR-4 | TS-S9, TS-U4 |
| FR-6 | TS-S13, TS-U6 |
