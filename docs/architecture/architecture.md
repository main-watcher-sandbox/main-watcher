---
id: ARCH-001
type: architecture
status: proposed
state: target
owner: platform-team
reviewed: 2026-09-23
review_by: 2027-03-15
review_trigger: "more than 20 target repos, public webhook hosting becomes available, or GitHub ships a native merge-queue pause"
sources: [FR-1, FR-2, FR-3, FR-4, FR-5, FR-6, C-1, C-2, C-6, C-7, ADR-001, ADR-002, ADR-003, ADR-004, ADR-007, ADR-008, ADR-009, ADR-010, ADR-011, ADR-012, ADR-013, ADR-014, ADR-015, ADR-016, ADR-017, ADR-018, ADR-019, ADR-020, ADR-021]
confidence: assumed
---

# Main Watcher — Architecture

## Confirmation queue

These items started as defaults chosen during design. The requester settled CQ-1 to CQ-9
on 2026-09-15. CQ-10 to CQ-14 came from adversarial reviews of the architecture on the
same day, and the requester confirmed all of them as proposed.

| # | Item | Section | Why it matters | Settled by |
|---|---|---|---|---|
| CQ-1 | Each target has a configurable `poll_interval` (default 15 min): the minimum time between test starts. The worker checks for changes every `check_period` (default 60 s). Confirmed | 2, 5.1 | Detection time vs. test load | Requester |
| CQ-2 | All target repos live in the watcher's GitHub organisation. Confirmed | 8, 10 | One installation per App | Requester |
| CQ-3 | Each target repo manages its own test secrets, its own way. Confirmed; this became FR-5 and ADR-009 | 8 | Who controls test credentials | Requester |
| CQ-4 | Failed tests are retried once before a failure counts. Confirmed | 5.1 | Flaky tests would otherwise lock the queue | Requester |
| CQ-5 | Setup, checkout and timeout errors alert, but do **not** lock the queue. Confirmed | 5.1, 11 | Watcher faults must not block teams | Requester |
| CQ-6 | The issue @-mentions owners only: `notify` in `targets.yml`, else the `*` owners in CODEOWNERS. Confirmed | 5.1 | Notification noise | Requester |
| CQ-7 | The gate fails **open** on API errors; the watcher reports it afterwards (ADR-008). Confirmed | 5.2, 11 | Merge availability | Requester |
| CQ-8 | The platform team owns the watcher repo, the trigger worker and all three GitHub Apps. Confirmed | 14 | Key custody | Requester |
| CQ-9 | Anyone with triage rights may apply `fixes-main`. Confirmed | 8, R-4 | The lock bypass | Requester |
| CQ-10 | The Reporter writes the lock issue before completing the check run, and replays an interrupted report without re-locking a commit a human overrode; a "reporting pending" alert after 15 min. The test outcome comes from the `main-watcher-test` step (build and tests), so a failure stays red without CTRF; checkout and restore failures, and tests that do not finish (deadline, timeout, cancellation), are infrastructure errors; a test run that waits over 30 min for a runner, or overruns its job timeout, is cancelled and its steps read before it is judged (ADR-013). Confirmed | 5.1 | A crash or a failed upload must not leave a red `main` unlocked, and a replay must not undo an override | Requester |
| CQ-11 | A lock lapses when the watcher has not renewed it for `lock_lease` (default 4 h), and the gate then fails open with a warning. With a lock open, NFR-3 therefore holds only after up to `lock_lease`. `mw-observer` gains Issues: read (ADR-014). Confirmed | 5.2, 8 | Merge availability vs. enforcing a red `main` through a long watcher outage | Requester |
| CQ-12 | Reconciliation continues after a lock closes, until merges up to its closure are checked; closures older than 30 days are not revisited; each merge is judged by the PR's labels at merge time (ADR-015). Confirmed | 5.2 | NFR-4 must hold when a human closes the lock first, or a label changes after the merge | Requester |
| CQ-13 | When a lock opens, or a lapsed lock is renewed, the watcher re-runs the gate for merge groups still in the queue whose gate started earlier. FR-4 is narrowed: a group that merges in the seconds before its re-run takes effect is reported, not blocked (ADR-016). Confirmed | 2, 5.2 | Otherwise groups that passed the gate just before a lock merge onto a red `main`, with no outage involved | Requester |
| CQ-14 | A head whose newest result is `neutral` is tested again once `poll_interval` has passed, up to 3 neutral results per head; after that, a "head untestable" alert, and a push or a forced dispatch is needed (ADR-017). Confirmed | 5.1 | A setup failure must not leave `main` untested, and a lock wrongly open or closed | Requester |

---

## 1. Overview

Main Watcher is a reusable component that continuously runs a repository's .NET/xUnit
integration tests against the latest commit on `main`.

When tests fail, it opens a GitHub issue listing the failing tests and every push made to
`main` since the last green run. While that issue is open, the repository's GitHub merge
queue is blocked. The only PRs that can still merge are those labelled `fixes-main`.

It has four parts:

- **The trigger worker** — a small .NET service in the organisation's Kubernetes cluster.
  Every minute it checks, read-only, whether any target has a new `main` head or a finished
  test run. If so, it starts the watcher workflow. GitHub's own scheduler is too unreliable
  for this job (ADR-010).
- **The watcher repo** — decides what to test, starts the test workflow in each target,
  and reports results. It acts through the `main-watcher` GitHub App (ADR-001).
- **A test workflow in each target repo** — calls a shared, versioned workflow from the
  watcher repo. Because it runs inside the target repo, it uses that repo's own secrets
  (ADR-009).
- **A gate workflow in each target repo** — a required merge-queue check that fails queue
  entries while a lock issue is open (ADR-002). If it cannot reach the API it fails open,
  and the watcher reports any merges that slipped through (ADR-008). It also fails open
  when the watcher has stopped renewing the lock's lease (ADR-014).

Every test run also reports timings: suite duration, build vs. test time, and the slowest
tests across recent runs. These appear in the run's job summary and on the commit's check
(ADR-011). A metrics store of the organisation's own is deferred.

Main Watcher has no datastore; GitHub holds all state (ADR-003):
- the lock is the open issue;
- the test outcome for each commit is a check run;
- the link to a running test is stored on that check run.

## 2. Drivers

| Driver | Type | Source | Consequence for the design |
|---|---|---|---|
| FR-1 Reusable; can be pointed at any GitHub repo | Fact | Requester | Onboarding = config entry + two workflow files + App installs |
| FR-2 Runs integration tests continuously on `main`, testing only the latest commit when pushes pile up | Fact | Requester | Change detection by the worker; one run per target at a time (ADR-001, ADR-010) |
| FR-3 On failure, opens an issue listing failing tests and all pushes since the last successful run | Fact | Requester | CTRF results (ADR-007); repository activity API |
| FR-4 Blocks the GitHub merge queue until the issue is resolved | Fact | Requester | Gate check (ADR-002); resolution rules (ADR-004); groups queued before a lock are re-checked, and a race of seconds is reported (ADR-016, CQ-13) |
| FR-5 Each target repo manages its tests' secrets its own way | Fact | Requester, 2026-09-15 | Tests run inside the target repo (ADR-009) |
| FR-6 Capture how long integration tests take, and identify the slowest tests in a suite | Fact | Requester, 2026-09-15 | Timings and reports in the shared workflow (ADR-011); own store deferred |
| C-1 GitHub's merge queue has no pause function | Constraint | Community discussion 50893; vendor comparison | Pause built from a required check |
| C-2 While `main` is locked, a fix must still be able to merge | Constraint | Design analysis | `fixes-main` bypass |
| C-3 "Restrict updates" ruleset bypass lists reportedly don't work | Risk-bearing fact (user reports) | Community discussions 113172, 196766 | Freeze ruleset rejected (ADR-002) |
| C-4 The only queue in use is GitHub's own merge queue | Constraint | Requester | No Mergify adapter |
| C-5 Every target repo uses .NET with xUnit: the `xunit.v3` package, 3.x or 4.x (ADR-021) | Constraint | Requester | CTRF contract (ADR-007, ADR-021) |
| C-6 .NET shop; no Azure subscription; containers can be hosted on Docker/Kubernetes | Constraint | Requester, 2026-09-15 | Self-hosted worker (ADR-010) |
| C-8 No monitoring, alerting or database infrastructure exists | Constraint | Requester, 2026-09-15 | Alerts via GitHub issues (ADR-012); metrics phase 1 stays inside GitHub (ADR-011) |
| C-7 GitHub scheduled workflows are delayed and sometimes dropped | Fact | GitHub troubleshooting docs; requester experience | The schedule is only a backup sweep (ADR-010) |
| NFR-1 Time from a bad push to an open issue ≤ `poll_interval` + suite duration + about 2 × `check_period` | Assumption | Derived | About 17 min plus suite time with defaults, while the worker is healthy |
| NFR-2 A healthy `main` adds no delay to merges | Decision | D2 discussion | The gate decides locally |
| NFR-3 Neither a watcher/worker outage nor a GitHub API error blocks merges | Decision | Requester | Fail-open by construction (ADR-008); an open lock lapses after `lock_lease` without renewal (ADR-014, CQ-11) |
| NFR-4 Every merge of an unlabelled PR during a lock is reported | Decision | Requester | Reconciliation through each lock's closure (ADR-008, ADR-015) |

### Quality attributes, in priority order

| Rank | Attribute | Target and conditions | How it will be verified |
|---|---|---|---|
| 1 | Correctness of the lock | Main is never locked without an App-authored issue, never stays locked after a green run, and an interrupted report still opens the lock; a group queued before a lock does not merge after it, outside a race of seconds | TS-S3–S6, TS-S14, TS-S17 |
| 2 | Availability of merging | No component outage blocks merges for longer than `lock_lease` (4 h) even with a lock open; a false lock can be overridden in under 5 min | TS-S7, TS-S9 |
| 3 | Least privilege | The worker holds only read and dispatch rights; target code never runs near the main App key | TS-S8 |
| 4 | Timeliness | NFR-1 while the worker is healthy; at most about 1 h + suite time while it is down | TS-S11, alert timestamps |
| 5 | Cost | No GitHub Actions run when nothing changed | Actions usage report |

### Assumptions

| # | Assumption | Impact if wrong | Owner | Confirm by |
|---|---|---|---|---|
| A-1 | Fewer than 20 target repos | Worker API usage and dispatch volume grow; revisit ADR-010 | Platform lead | 2026-10-15 |
| A-2 | Test suites finish in under 30 minutes | Slower detection; more superseded commits | Platform lead | Onboarding |
| A-4 | Target test workflows run on GitHub-hosted runners, or on self-hosted runners the target team owns | None for the watcher; isolation is the target team's concern | Target owners | Onboarding |
| A-5 | The gate can identify every PR in a merge group. **Tested in the sandbox on 2026-09-16 (TS-S5):** `base_sha...head_sha` does not work, because for a queue entry behind another, `base_sha` is the head of the entry ahead of it. The gate therefore compares the queue's target branch (`base_ref`) with `head_sha` | The gate cannot enforce grouped merges | Platform lead | Confirmed in the sandbox with the target-branch comparison; TS-S5 at onboarding |
| A-6 | The cluster has outbound HTTPS to `api.github.com` and a secret store (no alerting stack; see C-8). Confirmed 2026-09-16: a pod in namespace `main-watcher-sandbox` got HTTP 200 from `api.github.com`; the sandbox holds the App keys in a plain Kubernetes Secret, and production will use the cluster's secret store | The worker cannot run there | Platform team | Confirmed 2026-09-16 |
| A-7 | Re-running a successful required check makes it pending again for the merge group, and a failed re-run removes the group; queued groups appear as `gh-readonly-queue/main/*` branches. **Confirmed in the sandbox on 2026-09-16 (MainWatcher#6):** with a second required check still running, re-running the passed gate added a new in-progress gate check run on the group's commit, and the group stayed queued. When a lock had opened, the re-run failed and the queue removed the group 2 s later (`failed_checks`), 6 minutes before the other check finished. When no lock was open, the re-run passed and the group merged normally. Queue branches are named `gh-readonly-queue/<branch>/pr-<number>-<base sha>`. The re-runs were requested with a user token, not the App. **TS-S17 confirmed the same through the watcher's own sweep and the `main-watcher` App on 2026-09-18 (MainWatcher#21):** a group whose gate had passed while `main` was green was removed 39 s after the lock opened, with its slow second check still four minutes from finishing | ADR-016 cannot stop groups that passed before a lock; replace it with its Option B or D | Platform lead | Confirmed by the spike and by TS-S17, which re-checks it on every release |

### Scope

**In scope:**
- testing the latest `main` commit of each configured repo;
- the lock issue's lifecycle;
- the gate;
- the trigger worker;
- onboarding documentation.

**Out of scope:**
- PR-branch testing;
- automatic re-queueing of PRs the gate removed (§17);
- non-GitHub merge queues;
- bisecting to find the culprit commit (§17);
- webhook-driven triggering (§17).

### Domains considered

| Domain | Status |
|---|---|
| Context/scope, structure, governance | In play |
| Integration (GitHub APIs) | In play; critical |
| Identity and security | In play; §8. No separate threat model |
| Runtime, observability, deployment | In play: a self-hosted worker now exists (§10, §11) |
| Quality and testing | In play; TS-001 |
| Data | Light; GitHub holds all state |
| Performance and scale | Set aside because of A-1; rate limits are tracked as R-13 |
| Delivery and teams | Set aside because of CQ-8 |
| Migration | Set aside; this is new work |
| Cost | Light; Actions minutes run only when there is work |

## 3. System context

This diagram answers: who interacts with Main Watcher, and what does it depend on?

```mermaid
%% name: system-context
flowchart LR
    dev(["Developer<br/>[person]"])
    owner(["Repo owner / fixer<br/>[person]"])
    plat(["Platform team<br/>[person]"])

    subgraph boundary["Main Watcher"]
        sys["Main Watcher<br/>[software system]"]
    end

    gh["GitHub<br/>[external: API, Actions, merge queue]"]
    target["Target repository<br/>[external, on GitHub]"]
    k8s["Kubernetes cluster<br/>[external, org-hosted]"]

    dev -->|"pushes, queues PRs"| target
    owner -->|"fixes, labels, closes issue"| target
    target --- gh
    sys -->|"reads state, starts runs,<br/>writes checks and issues<br/>REST/HTTPS"| gh
    sys -->|"runs trigger worker"| k8s
    sys -->|"watcher-infra issues"| gh
    plat -->|"watches watcher-infra issues"| gh

    classDef person fill:#08427b,stroke:#052e56,color:#fff
    classDef internal fill:#1168bd,stroke:#0b4884,color:#fff
    classDef external fill:#999,stroke:#6b6b6b,color:#fff
    class dev,owner,plat person
    class sys internal
    class gh,target,k8s external
```

**GitHub is critical.** When its API is down:
- the worker and the watcher retry, then give up without changing any lock (CQ-5);
- the gate fails open (ADR-008), and the watcher reports any harm afterwards.

**The Kubernetes cluster matters for timeliness, not correctness.** While the worker is
down, the hourly sweep still does the work (ADR-010).

## 4. Solution overview

This diagram answers: what are the pieces, where do they run, and who calls whom?

```mermaid
%% name: container-view
flowchart LR
    subgraph cluster["Kubernetes cluster"]
        worker["Trigger worker<br/>.NET BackgroundService"]
    end

    subgraph watcher["Watcher repo"]
        cfg[("targets.yml")]
        watch["watch.yml<br/>Planner + Reporter"]
        reuse["run-integration-tests.yml<br/>reusable workflow"]
    end

    subgraph targetRepo["Each target repo"]
        tests["main-watcher-tests.yml"]
        gate["Gate workflow"]
        mainb[("main")]
        lock[("Lock issue")]
    end

    worker -->|"read, via mw-observer"| cfg
    worker -->|"read heads, checks, runs<br/>via mw-observer"| mainb
    worker -->|"read lock leases<br/>via mw-observer"| lock
    worker -->|"workflow_dispatch<br/>via mw-doorbell"| watch
    watch -->|"workflow_dispatch sha<br/>via main-watcher App"| tests
    tests -->|"uses, secrets: inherit"| reuse
    watch -->|"download CTRF artifact"| tests
    watch -->|"check runs, issues"| lock
    gate -->|"read lock state"| lock
```

| Container | Responsibility | Technology | Owner | Serves |
|---|---|---|---|---|
| targets.yml | Lists targets: repo, test command, CTRF results glob, timeout, `poll_interval`, `notify`, `enabled` | YAML in watcher repo | Platform team | FR-1 |
| Trigger worker | Every `check_period`, detects work per target (eligible head, ADR-017; finished `main-watcher` job, stale run, lock lease due for renewal, closed lock not yet reconciled, unfinished queue sweep) and starts `watch.yml`. Exposes `/healthz`. Raises `watcher-infra` issues if the watcher hasn't completed a run in 2 h, if reporting has been pending, or a queue sweep unfinished, for more than 15 min, on repeated errors, or on token failures (ADR-012, ADR-013, ADR-014, ADR-016) | .NET 10 `BackgroundService`, container, 1 replica | Platform team | FR-2, C-7 |
| watch.yml — Planner | For the targets passed in (or all, on the hourly sweep): for an eligible head (ADR-017), creates an in-progress check run, starts the target's test workflow with `return_run_details`, stores the run ID in the check run's `external_id`. Handles stale runs: cancels a run past its queue or run deadline, and reports it once it has stopped (ADR-013); renews lock leases (ADR-014); finishes queue sweeps (ADR-016); reconciles merges made during a lock, through the lock's closure, judging labels at merge time (ADR-008, ADR-015); raises "worker appears down" if work waited more than 15 min | GitHub Actions job running the .NET watcher scripts | Platform team | FR-2, NFR-3, NFR-4 |
| watch.yml — Reporter | When a target run's `main-watcher` job has completed (other jobs in the run are ignored): reads the outcome of the `main-watcher-test` step, trusted only when the `main-watcher-tests-finished` marker step succeeded; a job whose steps GitHub has not yet written down is read again rather than judged, for up to 5 minutes after it completed (ADR-019); downloads CTRF, finds the last green commit, collects pushes, opens, updates or closes the lock issue (never re-locking a commit a human overrode), and only then completes the check run, so an interrupted report is replayed (ADR-013). Opening a lock records the queue-sweep obligation in the same write, which the Planner discharges later in the same cycle (ADR-016). Adds a timing section to the check run: suite time, change from last green, 5 slowest tests, retry flag (ADR-011) | GitHub Actions job running the .NET watcher scripts | Platform team | FR-3, FR-4 |
| run-integration-tests.yml | `main-watcher` job: checks out `sha` and restores (setup steps); builds and runs tests with one retry of failed tests in a single step, `main-watcher-test`, through a wrapper that enforces the target's timeout; a marker step, `main-watcher-tests-finished`, succeeds only if the tests ran to completion (ADR-013); then writes `timings.json` and uploads the `main-watcher-ctrf` artifact, even when that step failed. `report` job: no secrets, `actions: read` and `contents: read`, publishes the CTRF job summary with the slowest tests and duration trends, reading earlier runs from the `main-watcher-report` artifact it uploads (ADR-011, ADR-018) | Reusable GitHub workflow, version-tagged; `ctrf-io/github-test-reporter` pinned by SHA | Platform team | FR-2, FR-6, ADR-007 |
| main-watcher-tests.yml | Caller: `workflow_dispatch` inputs → reusable workflow, `secrets: inherit`. The place where the target sets up OIDC or feeds | ~15-line workflow in target | Target owners (template from platform) | FR-5 |
| Gate workflow | On `merge_group`: fails while an App-authored lock with an unexpired lease is open, unless every PR in the group has `fixes-main`. Fails open, with a warning, on API errors or an expired lease (ADR-008, ADR-014). On `pull_request`: always passes | ~40-line workflow in target | Target owners (template from platform) | FR-4, C-2 |
| main | The branch under test | Git branch | Target owners | FR-2 |
| Lock issue | The pause signal and the human report | Issue labelled `main-broken`, authored by `main-watcher[bot]` | Target owners | FR-3, FR-4 |
| GitHub Apps | `main-watcher` (acts), `mw-observer` (reads), `mw-doorbell` (starts `watch.yml`, raises alert issues) | GitHub Apps | Platform team | §8 |

## 5. Key flows

### 5.1 Push → test → lock (FR-2, FR-3, FR-4)

This diagram answers: what happens between a bad push and a blocked queue?

```mermaid
%% name: seq-failure-to-lock
sequenceDiagram
    autonumber
    participant W as Trigger worker
    participant GH as GitHub API
    participant WA as watch.yml
    participant T as Target test run

    loop every check_period
        W->>GH: read main head and newest check runs
    end
    Note over W: eligible head: no check run, or<br/>neutral and retryable (ADR-017)
    W->>GH: dispatch watch.yml, targets
    GH->>WA: start run
    WA->>GH: create check run in_progress
    WA->>GH: dispatch tests, sha, return_run_details
    GH-->>WA: run id
    WA->>GH: set check run external_id
    GH->>T: start run in target repo
    T->>T: build, test, retry failed once
    T->>GH: upload CTRF artifact, complete
    W->>GH: read check run, main-watcher job status
    Note over W: main-watcher job completed
    W->>GH: dispatch watch.yml, targets
    GH->>WA: start run
    WA->>GH: read test and finished-marker steps, download CTRF
    alt tests did not finish, or run deleted
        WA->>GH: infra alert
        WA->>GH: check run neutral
    else test step failed, with or without CTRF
        WA->>GH: list lock issues, open and closed
        WA->>GH: find newest green check run on main
        WA->>GH: GET activity ref=main since green
        alt result already on a closed lock, or commit overridden
            Note over WA: override stands, no new lock
        else no open lock issue
            WA->>GH: create issue main-broken, lease_until
            WA->>GH: re-run gate of queued groups checked before the lock
        else lock issue open
            WA->>GH: update body, add comment, skip if already done
        end
        WA->>GH: check run failure
    else test step passed
        WA->>GH: close open lock issue, if any
        WA->>GH: check run success
    end
    Note over WA,GH: check run completed last.<br/>An interrupted report is replayed<br/>on the next cycle (ADR-013)
```

**Latest only.** While a target has an in-progress check run, the worker flags no new
head for it. When the run completes, the next cycle tests whatever `main` is at that
moment; intermediate commits are never tested. A head whose newest result is `neutral` is
tested again after `poll_interval`, up to 3 neutral results; then a "head untestable" alert
is raised (ADR-017).

**Failure behaviour.**

- **Target run cancelled, deleted, or never started.**
  - Detection: the check run stays `in_progress` while the target run's `main-watcher` job
    does not complete, or its `external_id` points to a run that no longer exists. A
    completed job is never stale, even without an artifact or while other jobs in the run
    are unfinished (ADR-013).
  - A job that has not started 30 min after the check run was created, or is still running
    its `timeout-minutes` + 10 min after it started, is cancelled first, and force-cancelled
    if it will not stop. The check run stays `in_progress` meanwhile, however long that
    takes: a run that cannot be stopped raises an alert and blocks testing on that target
    until it stops or is deleted. Once the job has
    stopped, it is reported from the test step if `main-watcher-tests-finished` succeeded,
    and is otherwise `neutral` (ADR-013).
  - A `watcher-infra` alert is raised. The head stays eligible and is retested after
    `poll_interval`; there is no separate retry step for a crash to lose (ADR-017).
- **Reporter interrupted** (crash, cancelled job, or API retries exhausted mid-report).
  - The check run is completed only after the lock issue is written, so it stays
    `in_progress` with a completed `main-watcher` job: "reporting pending" (ADR-013).
  - The worker flags that as work on the next cycle and the Reporter replays, skipping
    writes already made for this check run.
  - A pending report is never marked stale. After 15 min the worker raises a
    `watcher-infra` alert, "reporting pending".
- **Duplicate `watch.yml` runs.** Harmless: a concurrency group keeps one pending run, and
  a head is started only while it is eligible, checked again inside the concurrency group
  (ADR-017).
- **Worker down.** The hourly sweep at minute 17 processes all targets. If it finds work
  older than 15 minutes, it raises "trigger worker appears down" (ADR-010).
- **No green run exists yet, or the green commit was force-pushed away.** The push list
  falls back to activity after the green check run's timestamp, or to the last 100
  pushes. The issue says which fallback was used.
- **Test step failed without valid CTRF**, including when the upload or download failed or
  timed out, or the run was cancelled after the test step finished.
  The result is still red, because the outcome comes from the `main-watcher-test` step's
  conclusion, confirmed by the `main-watcher-tests-finished` marker, not from the artifact. The issue says "failing tests unknown" and links to
  the target run (ADR-007, ADR-013).
- **Setup step failed, or the tests did not finish** (checkout, toolchain or restore
  failure; test deadline, timeout, cancellation or lost runner). An infrastructure error: `neutral`,
  with an alert and no lock (CQ-5, ADR-013). Retested like any neutral result (ADR-017).
- **Duplicate reporters.** Before creating a lock, the Reporter lists App-authored
  `main-broken` issues in any state. It creates nothing if this check run is already on a
  closed lock, or if a human closed the latest lock for this same commit (an override,
  ADR-004). `watch.yml` concurrency serialises Reporters. If two open locks ever exist,
  the newer one is closed as a duplicate (ADR-013).

**Issue content:**

- **Body (current state):**
  - the failing commit;
  - the failing tests (name, suite, first line of `message`, truncated to 200 chars);
  - a link to the target run;
  - a push table: time, pusher (plain name, no `@`), type (push / force push / PR merge /
    merge-queue merge), before→after, commit count;
  - hidden markers `<!-- main-watcher last_green=… first_red=… last_reconciled=…
    lease_until=… reported_check=… reported_sha=… reconciled=… reconciled_commits=…
    sweep_required=… queue_swept=… lapsed=… lapse_reported=… -->` (ADR-013, ADR-014,
    ADR-015, ADR-016).
- **Comments (history):** one per later failing run, each with a hidden `check=` marker
  for replay (ADR-013). Comments do not mention anyone.
- **Mentions (CQ-6):** the body opens by mentioning the target's `notify` list, or else the
  owners of the `*` rule in CODEOWNERS. If neither exists, nobody is mentioned, and a
  `watcher-infra` warning asks for `notify` to be configured. A team handle notifies its
  members only where the App holds organisation `Members: read`; without it the mention still
  renders as a team link, but nobody is notified (R-10, TS-S10).

### 5.2 Merge group while locked (FR-4, C-2, ADR-008)

This diagram answers: how does the gate block ordinary PRs but let fixes through?

```mermaid
%% name: seq-gate
sequenceDiagram
    autonumber
    actor M as Maintainer
    participant MQ as Merge queue
    participant G as Gate workflow
    participant GH as GitHub API

    M->>MQ: queue PR
    MQ->>G: merge_group event, base_sha, head_sha
    G->>GH: list open issues label main-broken
    alt API unreachable after 3 retries
        G-->>MQ: success, warning LOCK STATUS UNKNOWN
    else no issue authored by main-watcher
        G-->>MQ: success
    else lock open, lease expired or invalid
        G-->>MQ: success, warning LOCK LEASE EXPIRED
    else lock open, lease valid
        G->>GH: compare base_ref...head_sha, find PRs
        alt every PR labelled fixes-main
            G-->>MQ: success
        else any PR unlabelled
            G-->>MQ: failure, link to lock issue
            MQ->>MQ: remove PR from queue
        end
    end
```

**Notes:**
- **Only App-authored issues lock the queue.**
- **On `pull_request` events the gate always passes.**
- **Implementation.** Targets copy `templates/main-watcher-gate.yml`. Its
  `main-watcher-gate` job runs the gate action from the watcher repo at a pinned tag, which
  builds `src/MainWatcher.Gate`. The group's PRs are the open PRs into the queue's branch
  that are associated with a commit on `head_sha` not yet on the target branch (`base_ref`), named in a merge or
  squash commit subject, or named in the queue branch; including an extra PR can only make
  the gate stricter. When the gate fails open, a second job named
  `main-watcher/gate-fail-open` runs, so the fail-open check run needs no `checks: write`.
- **PRs removed by the gate** must be re-queued by hand (§17). The unlock comment lists
  them.
- **Groups already queued when a lock opens (ADR-016).** A group whose gate started before
  the lock existed would otherwise merge as soon as its other checks pass. After opening a
  lock, or renewing a lapsed one, the watcher lists the groups still in the queue
  (`gh-readonly-queue/main/*` branches) and re-runs each gate run that started before the
  obligation's time plus 5 minutes; a gate still running is re-run once it finishes, and one
  GitHub refuses to re-run leaves the sweep owed rather than passing the group. The
  obligation is recorded as `sweep_required` in the same write that opens the lock or renews
  its lease, and stays owed until `queue_swept` catches up, so a crash cannot drop it; the
  worker keeps requesting work until then. A group that merges before its re-run takes effect
  is reported by reconciliation.
- **PRs the gate removed** are named in the unlock comment from the failed `merge_group` gate runs
  whose failing attempt falls inside the lock's window, each identified by the PR in its queue
  branch (R-7). A group the queue sweep removed failed on a **re-run** of a run GitHub still dates
  by its first attempt, so the read reaches a day further back and the comment says which groups
  went that way (ADR-016).
- **Lock lease (ADR-014).** The watcher sets each open lock's `lease_until` to 4 h ahead
  whenever it processes the target, and the worker requests a renewal once a lease is an
  hour old. If the watcher stops, the lease runs out and the gate fails open with a
  warning, so a watcher outage blocks ordinary merges for at most `lock_lease`. When the
  watcher returns it renews the lease and records the lapse in the same write, then
  comments on the lapse, raises an alert, and re-checks groups queued during the lapse
  (ADR-016).
- **Reconciliation (ADR-008, ADR-015).** Every merge moves `main`, which makes the worker
  start `watch.yml`. For every App-authored lock issue that is open, or closed but not yet
  marked `reconciled=complete`, the Planner lists `merge_queue_merge` and `pr_merge`
  activity since `last_reconciled`, up to the issue's close time. Any PR that did not
  carry `fixes-main` when it merged, judged from its label events, is appended under
  "Merged while locked" (with a comment, if the issue is closed) and raised as a
  `watcher-infra` alert. A closed issue is marked complete once
  its whole window has been checked.
  **Attribution.** Each activity entry's commits are read with the compare API and the PR is taken
  from the commit subject, `Merge pull request #N from …` or `… (#N)`, the two shapes the gate
  matches. The commits-to-PRs API would be exact but needs a Pull requests permission the
  `main-watcher` App does not hold (§8), and ADR-008 promised no new permission. A merge whose
  subjects name no PR, or whose range can no longer be compared, is reported as the commit it left
  on `main`, not passed over. A PR is named by its last commit, so a part-read range loses what
  names the later PRs: one pass reads at most 500 commits of an entry, and a longer range keeps the
  entry in front of the cursor with `reconciled_commits` recording how far it got, so the next cycle
  finishes it and the lock is complete only once every PR has been judged. That marker holds every
  entry of the second the cursor is stuck on, finished ones included, because the cursor moves a
  whole second at a time and an entry whose progress was forgotten would be read again from the
  start. The labels are judged at the PR's
  own `merged` event, which is in the same timeline as the label events, not at the merge commit's
  date, which the queue writes before the group merges.

### 5.3 Resolution (ADR-004, ADR-014)

This diagram answers: what states can a target be in, and what moves it between them?

```mermaid
%% name: state-target-health
stateDiagram-v2
    [*] --> Green
    Green --> Locked : failing run on new head
    Locked --> Locked : failing run, comment added
    Locked --> Green : passing run, App closes issue
    Locked --> Overridden : human closes issue
    Locked --> Lapsed : lease expires, watcher not renewing
    Lapsed --> Locked : watcher renews lease
    Lapsed --> Overridden : human closes issue
    Overridden --> Green : passing run
    Overridden --> Locked : failing run on a newer head
    Green --> Green : passing run or infra error

    note right of Overridden
        Queue unblocked while main is red.
        The App comments with who closed it.
    end note

    note right of Lapsed
        Issue still open. Gate fails open
        with a warning. Merges are reconciled.
    end note
```

## 6. Data

Main Watcher has no datastore.

| Data | System of record | Derived copies | Classification | Retention |
|---|---|---|---|---|
| Target list | `targets.yml` in watcher repo | Worker's in-memory copy per cycle | Internal | Git history |
| Test outcome per commit | Check runs by `main-watcher` on the target commit; the newest is the result, and neutral ones are counted for retries (ADR-017) | Lock issue text | Internal | GitHub check retention |
| Link to running test | Check run `external_id` = target run ID | — | Internal | As above |
| Lock state | Open App-authored `main-broken` issue whose `lease_until` has not passed (ADR-014) | — | Internal | Issue history |
| Reporting pending | An `in_progress` check run whose target run's `main-watcher` job has completed (ADR-013) | — | Internal | GitHub check retention |
| Test outcome of a target run | Conclusions of the `main-watcher-test` step and the `main-watcher-tests-finished` marker step, read through the Actions jobs API (ADR-013) | Check run conclusion | Internal | Target repo's run retention |
| Last green commit, last reconciled activity, lease, last reported check, reconciliation complete, queue sweep | Check runs; hidden markers in the lock issue, open or closed | — | Internal | As above |
| Test logs, CTRF reports | Actions run and artifact **in the target repo** | Excerpts in the issue | Internal; may contain secrets if tests print them | Target repo's artifact retention |
| Test timings (`timings.json`: queue wait, step durations, wall time, summed test time, retry flag) | `main-watcher-ctrf` artifact in the target repo | Job summary; check run output | Internal | Target repo's artifact retention; phase 2 store deferred (ADR-011) |
| Job-summary history (the reporter's merged CTRF report per run) | `main-watcher-report` artifact in the target repo | Duration trends and slowest tests in later job summaries | Internal | Target repo's artifact retention; the last 100 runs are read (ADR-018) |
| Job summary markdown (the reporter's generated summary per run) | `main-watcher-summary` artifact in the target repo | TS-S13 checks it against the CTRF reports, since the job summary is not in the REST API | Internal | Target repo's artifact retention |

## 8. Security

**Trust boundaries:**
- **Target code** runs only in the target repo's own Actions runs, under that repo's
  secrets and permissions (ADR-009). It never runs in the watcher repo or in the cluster.
- **The trigger worker** runs in the organisation's cluster and holds only read and
  dispatch credentials (ADR-010).
- **The main App key** exists only in the watcher repo's `reporter` environment, used only
  by `watch.yml`, which executes no target code.

**GitHub Apps:**

| App | Installed on | Permissions | Key location | Worst case if the key leaks |
|---|---|---|---|---|
| `main-watcher` | Target repos only (R-11) | Metadata R, Contents R, Checks W, Issues W, Actions W; Members R on the organisation, only where a `notify` list or CODEOWNERS `*` rule names a team (R-10) | Watcher `reporter` environment | Fake or close locks; start, cancel or disable target workflows (R-11); read organisation membership |
| `mw-observer` | Target repos + watcher only (R-11) | Metadata R, Contents R, Checks R, Actions R, Issues R (ADR-014) | Kubernetes Secret | Read-only access to target code, run metadata and issues |
| `mw-doorbell` | Watcher only | Actions W, Issues W | Kubernetes Secret | Start, cancel or disable watcher workflows; create spam issues in the watcher repo (R-12) |

**Report job permissions (in the target's run):** `actions: read`, `contents: read`. It has
no secret references, and the third-party reporter action is pinned by commit SHA (R-16).

**Test job permissions (in the target's run):** `contents: read`, `actions: read`. The token
is passed only to the step that writes `timings.json`, to read when the run and the job
started; restore and tests never receive it (ADR-018).

**Gate workflow permissions:** `issues: read`, `pull-requests: read`, `contents: read`.

**Secrets inventory:**

| Secret | Stored in | Managed by |
|---|---|---|
| `main-watcher` private key | Watcher repo, `reporter` environment | Platform team |
| `mw-observer` and `mw-doorbell` private keys | Production: the cluster's secret store. Sandbox: a Kubernetes Secret in `main-watcher-sandbox` | Platform team |
| Anything the tests need (DB strings, feed tokens, OIDC trust) | The target repo, its own way (FR-5) | Target owners |

For cloud access from tests, targets should prefer OIDC (`id-token: write`) over stored
keys. That is a recommendation, not something the watcher enforces.

**OIDC is not reachable today** `[open]`. The reusable workflow's `main-watcher` job declares its own
`permissions`, and a called job's block caps what its steps get whatever the caller grants, so `id-token: write`
never reaches the tests. Adding it to that job is a breaking change — GitHub fails a run whose called job asks
for a permission the caller did not grant, so every existing caller would have to grant it in the same release —
and so needs its own decision, a new workflow tag and the caller template updated with it. Found while writing
`docs/onboarding.md` (MainWatcher#26); no target has asked for OIDC yet.

**Permission matrix:**

| Principal | Target code | Checks | Lock issue | Target workflows | Merge queue |
|---|---|---|---|---|---|
| Trigger worker | Read | Read | Read (lease, reconciliation state) | Read status | — |
| watch.yml | Read | Write | Create, update, close | Start, cancel stale runs, re-run the gate, read artifacts | — |
| Target test run | Read, execute | — | — | (itself) | — |
| Gate workflow | Read | Its own | Read | — | Pass/fail entries |
| Maintainer (triage+) | per repo | — | Close (override) | per repo | Apply `fixes-main` |

**Organisation setting:** the watcher repo must allow its reusable workflows to be used by
other repositories in the organisation.

**Audit:** check runs, issue events, label events, workflow run history, and the worker's
logs.

## 9. Integrations

| Dependency | Owner | Criticality | Interaction | On failure |
|---|---|---|---|---|
| GitHub REST API: commits, check runs, issues, compare | GitHub | Critical | Sync REST, App tokens | Retry with backoff. Faults never create a lock; an interrupted report is replayed on the next cycle (ADR-013) |
| Workflow dispatch API with `return_run_details` (Feb 2026) | GitHub | Critical for testing | Sync REST | Retry on the next cycle. Fallback if the run ID is missing: find the run by the `sha` input (R-14) |
| Actions jobs API (step conclusions) | GitHub | Critical for reporting | Sync REST | The report stays pending and is retried. If the run no longer exists: `neutral`, alert "outcome unknown" (ADR-013) |
| Actions artifacts API | GitHub | Degraded: failing test names and timings | Sync REST | After retries, a failed test step is still red, with "failing tests unknown" (ADR-007, ADR-013) |
| Repository activity API | GitHub | Degraded: push list only | Sync REST | Issue opens with "push list unavailable" and a compare link |
| GitHub scheduled events | GitHub | Backup only | Hourly cron | The worker is the primary trigger |
| GitHub merge queue + rulesets | GitHub | Critical for FR-4 | Required check on `merge_group` | Outside our control |
| Workflow re-run API and `gh-readonly-queue/main/*` branches | GitHub | Degraded: groups queued before a lock may merge | Sync REST | The sweep stays unfinished and is retried; alert after 15 min; any merges are reconciled (ADR-016) |
| Kubernetes cluster | Organisation | Degraded: timeliness | Hosting | Liveness restart; hourly sweep + "worker appears down" issue alert |
| `ctrf-io/github-test-reporter` action | Open-source project | Degraded: timing report only | Step in the `report` job | Tests and locking are unaffected; the job summary is missing |

All GitHub calls go through one adapter, `GitHubGateway`, in a .NET library shared by the
worker and the watcher scripts. The same library holds the eligibility rule (ADR-017), so
the worker and the Planner cannot disagree about which head to test.

## 10. Deployment

This diagram answers: what runs where, and over which connections?

```mermaid
%% name: deployment
flowchart LR
    subgraph org["Organisation infrastructure"]
        subgraph k8s["Kubernetes, namespace main-watcher"]
            pod["Deployment: trigger-worker<br/>replicas: 1"]
            sec[("Secret: app keys")]
        end
    end

    subgraph github["GitHub.com"]
        api["REST API"]
        runners["GitHub-hosted runners"]
    end

    sec --> pod
    pod -->|"HTTPS 443, outbound"| api
    api --> runners
```

**Trigger worker:**
- A container image built from the worker project and published to the organisation's
  registry.
- Deployed with a Helm chart or plain manifests: one replica, liveness probe `/healthz`,
  resource requests of about 50m CPU and 128Mi memory `[assumption]`.
- It needs no inbound network access.
- Scenario tests use a second deployment in the `main-watcher-sandbox` namespace.
- A single replica is enough because duplicate dispatches are harmless. More replicas
  would need leader election (ADR-010).

**Watcher repo:**
- Changes go through PRs.
- The reusable workflow and the templates are tagged (`v1`, `v2`), and targets pin a tag.

**Rollback:**
- Worker: redeploy the previous image tag.
- Workflows: revert the commit or move the tag.
- A single target: set `enabled: false` in `targets.yml`.

**Onboarding a repo** (the working procedure is `docs/onboarding.md`). The order keeps the repo
mergeable throughout: nothing is enforced until step 5, and no cycle runs until step 7.
1. Add the repo to the selected repositories of `main-watcher` and `mw-observer`. Never
   install either App on all repositories (R-11).
2. Add `main-watcher-tests.yml` and the gate workflow from their templates, and set up the
   tests' secrets in the repo (OIDC in preference to stored keys).
3. Add an entry to `targets.yml` with `enabled: false`. Watcher repo CI validates every entry.
4. Dry-run it: `dry-run.yml` checks the entry, both copied workflows and one real test of the
   head of `main`, and validates that run's CTRF artifact against the schema. It creates no
   check run, so nothing it does can lock a repo that is not ready.
5. Make the gate a required check in the repo's merge-queue ruleset.
6. Run sandbox scenario TS-S5 once, against this repo's queue settings.
7. Set `enabled: true`.

**Removing a repo:** delete its `targets.yml` entry, then remove it from the selected
repositories of `main-watcher` and `mw-observer` (R-11).

## 11. Cross-cutting concerns

**Observability:**

| Signal | Source | Alert goes to |
|---|---|---|
| 3 worker cycle errors in a row | Worker | `watcher-infra` issue (ADR-012) |
| No completed `watch.yml` run in 2 h | Worker check | `watcher-infra` issue (ADR-012) |
| Reporting pending for more than 15 min (ADR-013) | Worker check | `watcher-infra` issue |
| Queue sweep unfinished 15 min after a lock opened or was renewed (ADR-016) | Worker check | `watcher-infra` issue |
| Head untestable: 3 neutral results on the same head (ADR-017) | Planner | `watcher-infra` issue |
| Target run not stopped 15 min after force-cancel; testing on that target is blocked until it stops or is deleted (ADR-013) | Planner | `watcher-infra` issue |
| Hung worker | Kubernetes liveness probe | Automatic restart |
| "Trigger worker appears down" (work waited more than 15 min) | Hourly sweep | `watcher-infra` issue |
| Infrastructure error twice in a row for a target; stale or cancelled target run | Planner | `watcher-infra` issue |
| PR merged without `fixes-main` during a lock, including after the lock closed (ADR-008, ADR-015) | Planner | `watcher-infra` issue + lock issue |
| Lock lease renewed after it had lapsed (ADR-014) | Planner | `watcher-infra` issue + lock issue comment |
| A closed lock still unreconciled 24 h after closing (ADR-015) | Planner | `watcher-infra` issue |
| `gate-fail-open` check runs (API error or expired lease) | Hourly sweep | `watcher-infra` issue |
| Missing `notify` and CODEOWNERS | Reporter | `watcher-infra` issue |
| App token failures | Worker / watcher | `watcher-infra` issue |

All alerts arrive as de-duplicated `watcher-infra` issues in the watcher repo. The platform
team subscribes to that label.

**Test-duration metrics (FR-6, ADR-011, ADR-018):**
- **Per run:** the job summary shows the slowest tests (top 10 across up to 100 previous
  runs, ranked by 95th percentile, with the average beside it), each earlier run's
  wall-clock time as the duration trend, and flaky rates. `timings.json` adds queue wait,
  restore and build-and-test step times, and summed per-test time.
- **Per commit:** the check run shows suite time (wall clock and summed), change from the
  last green run, the 5 slowest tests of the run and the retry flag, in durations the watcher
  converts from CTRF milliseconds itself (R-17). The suite time is kept in a `suite_ms` marker
  in the check run's output, which is where the next run reads the last green time (ADR-003).
- **Measurement rules:**
  - wall-clock and summed per-test time are reported separately, because of xUnit's
    parallel execution;
  - retried runs are flagged;
  - "slowest" means across runs.
- **Not available in phase 1:** cross-repo views and slowdown alerts.

**Failing safe:**
- No component failure creates a lock.
- The gate fails open (ADR-008).
- A lock the watcher stops renewing lapses after `lock_lease` (ADR-014).
- A human can always close the lock issue (ADR-004).

**Cost:**
- GitHub Actions runs only when there is work: one short `watch.yml` run per test start or
  finish, plus 24 sweeps a day.
- Test minutes are billed to each target repo, within the same organisation.
- The worker makes about 3–4 read calls per target per minute, including one for lock
  issues, plus one jobs call while a test is running (R-13, ADR-013, ADR-014).
- A `watch.yml` cycle costs the `main-watcher` installation about 20 requests, shared by
  every target within its 5000 an hour (R-13, MainWatcher#60).

## 13. Technology and versions

| Technology | Used for | Version | Upgrade owner |
|---|---|---|---|
| .NET | Trigger worker | .NET 10 (`global.json` pins the SDK; decided 2026-09-17, MainWatcher#14) | Platform team |
| Octokit.NET or plain `HttpClient` | GitHub calls from the shared library (worker and watcher scripts) | Pinned | Platform team |
| Docker / Kubernetes | Worker hosting | Organisation standard | Platform team |
| GitHub Actions | watch.yml, reusable test workflow, gate | `ubuntu-latest` runners | Platform team |
| .NET console app using the shared library (decided 2026-09-15) | watch.yml Planner and Reporter | Same .NET version as the worker | Platform team |
| `actions/create-github-app-token` | Token minting in workflows | Pinned by commit SHA | Platform team |
| xUnit (`xunit.v3` package) | Test framework in all targets | 3.x or 4.x; the sandbox uses 4.0.0, as production does (ADR-021) | Target owners |
| CTRF | Test result contract | CTRF JSON schema | Platform team |
| `ctrf-io/github-test-reporter` | Job-summary timing reports | v1, pinned by commit SHA | Platform team |

## 14. Ownership

| Asset | Owns | Operates | Approves change |
|---|---|---|---|
| Watcher repo, three Apps and their keys, trigger worker | Platform team | Platform team | Platform lead |
| `targets.yml` entry | Target owners | Platform team | Platform team + target owner |
| Test caller workflow, test secrets, gate workflow, ruleset | Target owners | GitHub | Target repo admin |
| Lock issues | Target owners | Main Watcher | — |

## 15. Decisions

| ADR | Decision | Status | Review trigger |
|---|---|---|---|
| ADR-001 | Central watcher tests only the newest `main` commit | Accepted, amended by ADR-009 and ADR-010 | More than 20 targets |
| ADR-002 | Pause the merge queue with a gate workflow in each target repo, bypassed by `fixes-main` | Accepted, amended by ADR-008, ADR-014 and ADR-016 | GitHub ships a native queue pause |
| ADR-003 | No datastore; check runs and the lock issue hold all state | Accepted, amended by ADR-013 and ADR-017 | Walk-back above ~50 calls |
| ADR-004 | Green run closes the lock automatically; a human close is an override | Accepted | Frequent overrides |
| ADR-005 | Test script contract: exit code plus JUnit XML | Superseded by ADR-007 | — |
| ADR-006 | GitHub App identity with split tokens | Superseded by ADR-009 | — |
| ADR-007 | Test script contract: exit code plus CTRF JSON | Accepted, amended by ADR-021 | A non-xUnit target appears |
| ADR-008 | Gate fails open on API errors; the watcher reconciles merges made during a lock | Accepted, amended by ADR-014 and ADR-015 | More than one unlabelled merge during a lock per quarter |
| ADR-009 | Tests run as a workflow in each target repo, started by the watcher | Accepted | Security rejects `actions: write` on targets |
| ADR-010 | Self-hosted .NET trigger worker; GitHub schedule only as an hourly backup | Accepted, amended by ADR-012, ADR-013, ADR-014, ADR-017 and ADR-020 | Webhook hosting becomes available |
| ADR-011 | Test-duration metrics phase 1 in GitHub (job summary, check run, `timings.json`); own store deferred | Accepted, amended by ADR-018 | Need for cross-repo views or alerts |
| ADR-012 | The worker alerts through `watcher-infra` GitHub issues | Accepted | A monitoring stack is adopted |
| ADR-013 | The Reporter completes the check run last; an interrupted report is replayed | Accepted (CQ-10), amended by ADR-019 and ADR-020 | "Reporting pending" alert more than once a month |
| ADR-014 | A lock is enforced only while the watcher renews its lease | Accepted (CQ-11) | A lock lapses more than once a quarter |
| ADR-015 | Reconciliation follows each lock through its closure, judging labels at merge time | Accepted (CQ-12) | A closed lock unreconciled 24 h after closing |
| ADR-016 | Opening a lock re-runs the gate for merge groups already in the queue; FR-4 narrowed to report a race of seconds | Accepted (CQ-13); built and TS-S17 passed on 2026-09-18 (MainWatcher#21), which confirmed A-7 through the App | TS-S17 disproves A-7 |
| ADR-017 | A head whose newest result is neutral is tested again after a wait, up to 3 times | Accepted (CQ-14) | "Head untestable" more than once a month |
| ADR-018 | The job summary keeps its history in `main-watcher-report`; the test job has `actions: read` for the timings step; previous-results report as the duration trend | Accepted | Reporter pin updated |
| ADR-019 | A completed job is judged only once GitHub has written its steps down, waiting at most 5 minutes | Accepted (MainWatcher#65) | A job's steps take more than a minute to become final |
| ADR-020 | The worker cancels a `watch.yml` run for a target that has not started after 20 minutes; the `reporter` environment has no required reviewers | Accepted (MainWatcher#67); built and TS-S19 passed on 2026-09-23 | The "watch.yml run stuck" alert more than once a month |
| ADR-021 | Targets may use xUnit 4.x: its CTRF option, one shared `TestResults/` folder, and `suite` as a string or an array | Accepted (MainWatcher#71); built, and the scenario suite passed on 4.0.0 targets on 2026-09-23 | A target on another xUnit major version, or a renamed CTRF option |

## 16. Risks and open questions

| # | Risk or question | Impact | Likelihood | Mitigation / default | Owner |
|---|---|---|---|---|---|
| R-1 | Flaky tests lock the queue for no reason | High | Medium | One retry (CQ-4); override; track flake rate per test | Target owners |
| R-2 | Gate blocks while the watcher is broken | High | Low | Only App-authored issues lock; faults never create locks (CQ-5); an existing lock lapses after `lock_lease` without renewal (ADR-014) | Platform team |
| R-3 | Batched merge groups let a non-fix PR merge alongside a fix | Medium | Medium | Gate checks every PR in the group; sandbox test (A-5) | Platform team |
| R-4 | `fixes-main` is misused to bypass the lock | Medium | Low | Label events audited; reviews still apply (CQ-9) | Target owners |
| R-5 | Worker and hourly sweep both fail silently | Detection stops | Low | Liveness restart; worker missing-run issue; sweep "worker appears down" issue | Platform team |
| R-6 | Tests leak secrets into logs or issue text | Secret exposure | Low | Log masking; issue shows at most 200 chars of the first message line | Target owners |
| R-7 | PRs removed by the gate are forgotten after unlock | Slower delivery | High | Unlock comment lists them; automatic re-queue deferred | Platform team |
| R-8 | During an API outage, a non-fix PR merges onto a red `main` | Breakage worsens | Low | Reconciliation reports it, even if the lock is closed first (ADR-008, ADR-015) | Platform team |
| R-10 | An App's team @-mention may not notify the team | Owners miss the lock | Medium | **Closed 2026-09-18 (TS-S10, MainWatcher#23):** it does notify, provided the App holds organisation `Members: read`. Without it the mention renders as a team link and no member is notified, so the permission is an install requirement wherever a team is mentioned. The fallback of mentioning members was not needed | Platform team |
| R-11 | `actions: write` lets the main App cancel or disable target workflows | Misuse if the key leaks | Low | Key only in a protected environment; the App's actions are audited; rotate the key. Security approved Actions: write for `main-watcher` (dispatch, cancel, force-cancel, gate re-runs) and Issues: read for `mw-observer` on 2026-09-16, on condition that both Apps are installed only on the repos being watched (plus the watcher repo for `mw-observer`) | Platform team |
| R-12 | Doorbell key leak lets an attacker cancel or disable watcher workflows | Detection stops | Low | Key in a cluster secret with restricted access; the sweep's alert fires if runs are disabled; rotate the key | Platform team |
| R-13 | API usage outgrows the `main-watcher` installation's budget | Every cycle fails on 403 until the hour refills; nothing is tested or reported meanwhile | Medium: the scenario suite spent it with six to ten busy targets (MainWatcher#25) | Every target's `watch.yml` cycles share one installation's 5000 requests an hour, so that budget, not the worker's, bounds how many targets one installation serves. Each cycle logs its requests by endpoint and the lowest budget it saw, and raises a de-duplicated `watcher-infra` alert when less than 20% is left or GitHub refused a request on its rate limit, through the workflow token's separate budget (MainWatcher#60). A first read of the check runs stops at the last green run, which cut a cycle from about 210 requests to about 20 (measured, MainWatcher#60). The worker logs its own Apps' budgets and alerts below 20%. Conditional requests (ETags) `[recommendation]`, not yet worth it: a cycle's remaining reads are few and mostly change between cycles | Platform team |
| R-14 | The dispatch API does not return run details (API version change) | Runs cannot be linked | Low | Fallback: find the run by the `sha` input and dispatch time | Platform team |
| R-15 | A target team weakens its own test workflow | False green | Low | Accepted: the team owns its repo; the reusable workflow is pinned | Target owners |
| R-16 | The third-party reporter action is compromised | Tampered summaries; reads of repo contents | Low | SHA pin; secret-free job; read-only token; update only after review | Platform team |
| R-17 | CTRF durations are shown in the wrong unit by the reporter (an open issue on that project) | Misleading timing data | Medium | Sandbox check TS-S13; the check-run section uses our own conversion | Platform team |
| R-18 | Timing history is lost beyond artifact retention before phase 2 exists | Long-term trends unavailable | High | Accepted for phase 1; phase 2 can backfill only as far as retention allows | Platform lead |
| R-19 | A watcher outage longer than `lock_lease` unlocks a `main` that is still red | Breakage worsens | Low | Gate warning and `gate-fail-open` check run; merges during the lapse are reconciled; "lock lapsed" alert on recovery (ADR-014) | Platform team |
| R-20 | An issue write keeps failing, so a report stays pending and no newer head is tested | Detection stalls for that target | Low | "Reporting pending" alert after 15 min (ADR-013) | Platform team |
| R-21 | A watcher outage longer than `reconcile_lookback` (30 days) leaves older closed locks unreconciled | Unlabelled merges go unreported | Very low | Accepted; worker and sweep alerts fire long before (ADR-015) | Platform team |
| R-22 | A merge group merges between a lock opening and its gate re-run, or GitHub changes merge-queue branch naming or re-run behaviour | A non-fix PR lands on a red `main` | Low | Sweep immediately after the lock opens; reconciliation reports it; TS-S17 on every release (ADR-016, A-7) | Platform team |
| R-23 | A head stays untestable after 3 neutral results, so a fixed `main` stays locked or a broken one stays unlocked | Merges blocked, or breakage unreported | Low | "Head untestable" alert; override; a push or a forced dispatch retests (ADR-017) | Platform team |
| R-24 | GitHub does not stop a stale target run even after force-cancel | Testing on that target stops until the run ends or is deleted | Very low | "Target run could not be stopped" alert; a person deletes the run (ADR-013) | Platform team |

## 17. Evolution

**Deferred decisions:**

| Decision | Trigger to revisit |
|---|---|
| Automatic re-queue of PRs the gate removed | R-7 complaints from more than 2 teams |
| Bisecting to name the culprit commit | Median suspect range above 5 pushes |
| Webhook receiver instead of polling (ADR-010 option C) | Public HTTPS hosting becomes available, or R-13 materialises |
| Worker leader election, multiple replicas | The worker's availability becomes a complaint |
| Metrics phase 2: PostgreSQL + Grafana fed by the worker; 90-day raw per-test rows plus permanent daily summaries (ADR-011) | Need for cross-repo views, slowdown alerts, or history beyond retention |
| Monitoring/alerting stack for the worker | The organisation adopts one (ADR-012) |

## 19. Glossary

| Term | Meaning |
|---|---|
| Target | A repository Main Watcher tests |
| Trigger worker | The self-hosted .NET service that detects work and starts `watch.yml` |
| Sweep | The hourly scheduled run of `watch.yml` that processes all targets |
| Green / red | The latest Main Watcher check run on a commit succeeded / failed |
| Lock issue | An open `main-broken` issue authored by the App; its presence blocks the merge queue |
| Override | A human closing the lock issue while `main` is still red |
| Lease | The time until which an open lock is enforced (`lease_until`). The watcher renews it; the gate ignores a lock whose lease has passed |
| Lapsed | A lock issue that is still open but whose lease has passed |
| Reporting pending | A check run still `in_progress` after its target run's `main-watcher` job completed; the Reporter replays it |
| Queue sweep | Re-running the gate for merge groups queued before a lock opened or was renewed (ADR-016) |
| Eligible head | A `main` head with no Main Watcher check run, or whose newest one is `neutral` and may be retried (ADR-017) |
| Gate | The required merge-queue check in the target that enforces the lock |

## 20. Change log

| Date | Change | By | Affected decisions |
|---|---|---|---|
| 2026-09-15 | Initial proposal | Platform team with Claude | ADR-001 to ADR-006 |
| 2026-09-15 | Result contract changed to CTRF; confirmation queue renumbered C-n → CQ-n | Platform team with Claude | ADR-007 supersedes ADR-005 |
| 2026-09-15 | CQ-1, CQ-5, CQ-7 settled; gate fails open with reconciliation | Platform team with Claude | ADR-008 amends ADR-002 |
| 2026-09-15 | CQ-2, CQ-4, CQ-6, CQ-8, CQ-9 settled; owner-only mentions | Platform team with Claude | — |
| 2026-09-15 | FR-5: targets own their test secrets; tests move into target repos; self-hosted trigger worker replaces the GitHub schedule; deployment diagram added; A-3 and R-9 removed | Platform team with Claude | ADR-009 (amends ADR-001, supersedes ADR-006), ADR-010 (amends ADR-001) |
| 2026-09-15 | FR-6 test-duration metrics (phase 1); C-8 no monitoring/DB infrastructure; A-6 corrected; worker alerts moved to GitHub issues; R-16–R-18 added | Platform team with Claude | ADR-011; ADR-012 (amends ADR-010) |
| 2026-09-15 | Adversarial review fixes, all proposed: reports complete the check run last and replay; lock lease bounds locks during watcher outages; reconciliation continues through closure. CQ-10–CQ-12, R-19–R-21, TS-S14, TS-S15 and TS-U8–U10 added; TS-S7 extended; `mw-observer` gains Issues: read | Platform team with Claude | ADR-013 (amends ADR-003, ADR-010); ADR-014 (amends ADR-002, ADR-008, ADR-010); ADR-015 (amends ADR-008) |
| 2026-09-15 | Second adversarial review: the test outcome is read from the `main-watcher-test` step, so a lost artifact no longer turns a failure neutral; replay checks closed locks and keeps human overrides. TS-S16 and TS-U11 added; TS-S14, TS-U5 and TS-U8 extended | Platform team with Claude | ADR-013 (revised while proposed) |
| 2026-09-15 | Third adversarial review: the test step is found by name, not step ID, with an explicit contract error; reconciliation judges labels at merge time; opening a lock re-runs the gate for groups already queued, with FR-4 narrowed to report a race of seconds. CQ-13, A-7, R-22, TS-S17 and TS-U12 added; TS-S15, TS-S16, TS-U10 and TS-U11 extended | Platform team with Claude | ADR-013 and ADR-015 (revised while proposed); ADR-016 (amends ADR-002) |
| 2026-09-15 | Fourth adversarial review: neutral results stay eligible for a retest after `poll_interval`, up to 3 per head; the queue-sweep obligation and the lease lapse are written in the same issue update as the lock or lease renewal. CQ-14, R-23, TS-S18 and TS-U13 added; TS-S12, TS-S17, TS-U5 and TS-U12 extended | Platform team with Claude | ADR-017 (amends ADR-003, ADR-010); ADR-014 and ADR-016 (revised while proposed) |
| 2026-09-15 | Fifth adversarial review: the test step's own conclusion takes precedence over a later cancellation or timeout, so a hung upload cannot hide a failure; upload steps get their own timeout. TS-S16 and TS-U11 extended | Platform team with Claude | ADR-013 (revised while proposed) |
| 2026-09-15 | Sixth adversarial review: GitHub reports a step timeout as `failure`, so the test step now runs through a deadline wrapper, and its result counts only when the `main-watcher-tests-finished` marker step succeeded; anything that interrupts the tests is neutral. TS-U14 added; TS-S16 and TS-U11 extended | Platform team with Claude | ADR-013 (revised while proposed) |
| 2026-09-15 | Seventh adversarial review: reporting and staleness follow the target run's `main-watcher` job, not the whole run, so a stuck `report` job cannot discard a proven failure; past the stale threshold, a succeeded finished-marker step is reported, not marked stale. TS-S16, TS-U5 and TS-U11 extended | Platform team with Claude | ADR-013 (revised while proposed) |
| 2026-09-15 | Eighth adversarial review: separate queue and run deadlines, the run deadline counted from the job's start; a stale run is cancelled, force-cancelled if needed, and judged from its steps once stopped, with no retest meanwhile. TS-U15 added; TS-S16 and TS-U5 extended | Platform team with Claude | ADR-013 (revised while proposed) |
| 2026-09-15 | Ninth adversarial review: a target run that cannot be stopped keeps its check run `in_progress`, blocking all testing on that target until it stops or is deleted; TS-U5 no longer exempts unfinished jobs with a succeeded marker from the deadlines. R-24 added; TS-S16, TS-U5 and TS-U15 extended. Not re-reviewed | Platform team with Claude | ADR-013 (revised while proposed) |
| 2026-09-15 | The requester confirmed CQ-10 to CQ-14 as proposed. A-7 stays an assumption until sandbox test TS-S17 | Requester; platform team with Claude | ADR-013, ADR-014, ADR-015, ADR-016 and ADR-017 accepted |
| 2026-09-15 | The `watch.yml` Planner and Reporter are written in .NET rather than Node, sharing one library with the trigger worker for `GitHubGateway` and the eligibility rule; the Node version assumption is removed (§4, §9, §13). TS-001 §2 and TS-U13 updated to test the rule once, in the shared library | Requester; platform team with Claude | — |
| 2026-09-16 | Sandbox organisation `main-watcher-sandbox` created; its `[assumption]` tag removed from TS-001 §3 | Platform team | — |
| 2026-09-16 | A-6 confirmed: outbound HTTPS to `api.github.com` works from the cluster; sandbox namespace `main-watcher-sandbox` created with the App keys in a Kubernetes Secret; production keys will use the cluster's secret store (§8, §10). TS-001 §2 and §3 name the namespace | Platform team | — |
| 2026-09-16 | Security approved Actions: write for `main-watcher` and Issues: read for `mw-observer`, on condition that the Apps are installed only on watched repos; recorded against R-11. §8 and §10 onboarding and removal steps updated; TS-001 §6 checklist extended | Security; platform team with Claude | ADR-009, ADR-013, ADR-014, ADR-016 (no change) |
| 2026-09-16 | Sandbox targets `sample-target` and `sample-target-slow` seeded from `sandbox/sample-target`: xUnit v3 on .NET 10 with Microsoft Testing Platform, one CTRF report per test project, outcomes steered by `sandbox.json`, merge queue on `main`. TS-001 §3 names them | Platform team with Claude | — |
| 2026-09-16 | Gate template v1 built (MainWatcher#5): `templates/main-watcher-gate.yml`, the gate action and `src/MainWatcher.Gate`; the `gate-fail-open` check run is a job in the template, so gate permissions stay read-only (§5.2). Watcher repo CI runs unit tests and actionlint | Platform team with Claude | ADR-002, ADR-008, ADR-014 (no change) |
| 2026-09-16 | Sandbox TS-S4 passed. TS-S5 disproved A-5 as worded: a queue entry's `base_sha` is the head of the entry ahead of it, so the gate now compares `base_ref...head_sha` and sees every PR in a batch (§3, §5.2). Sandbox targets use the gate from the public `main-watcher-sandbox/gate` repo, because a public target cannot use an action from a private repo | Platform team with Claude | ADR-002 (no change) |
| 2026-09-16 | A-7 confirmed in the sandbox (MainWatcher#6): re-running a passed gate while another required check waits keeps the group queued; a failed re-run removes the group within seconds; queue branch naming recorded (§3). Sandbox targets gain a second required check, `sandbox-slow-check` | Platform team with Claude | ADR-016 (no change; its review trigger is not raised) |
| 2026-09-16 | Reusable test workflow v1 and caller template built (MainWatcher#7): `run-integration-tests.yml`, `templates/main-watcher-tests.yml` and the deadline wrapper `src/MainWatcher.TestRunner`. The target's timeout is a workflow input; the job timeout adds 20 min through a lookup, as expressions have no arithmetic. Inherited secrets become environment variables for restore and tests. The retry appends `--ignore-exit-code 8 --filter-method …` to the test command, skips more than 100 failures, and folds its results into the first attempt's CTRF reports (`retries`, `flaky`). §4 row corrected: the job is `main-watcher`. In the sandbox, a caller's job is listed as `main-watcher-tests / main-watcher`, confirming ADR-013's `[assumption]` on called-workflow job names; passing and failing runs both uploaded `main-watcher-ctrf`, with the marker step `success` | Platform team with Claude | ADR-007, ADR-009, ADR-013 (no change) |
| 2026-09-16 | Manual watcher Planner and Reporter built (MainWatcher#9): shared .NET Core library, targets.yml validation, App-owned check dispatch/link/report lifecycle and CTRF failure output. Sandbox passing and failing checks verified and target restored. PR #37 review adds rejected/missing-dispatch recovery, per-target concurrency, caller configuration checks and incremental check discovery. | Platform team with Codex | ADR-007, ADR-009, ADR-013, ADR-017 (no change) |
| 2026-09-16 | PR #37 follow-up: commit discovery now shares Link-header pagination; a regression compares C# execution defaults with the reusable workflow; token scope uses the CLI configuration validator. Documented GitHub case-insensitive concurrency semantics and completed public API summaries. | Platform team with Codex | ADR-009, ADR-017 (no change) |
| 2026-09-16 | Test timings built (MainWatcher#8, FR-6). The `main-watcher` job writes `timings.json` into `main-watcher-ctrf` (schema 1, whole milliseconds, null when unknown): queue wait (run start to job start, from the Actions API), restore and build-and-test step times, the tests' wall-clock time from the first attempt's CTRF summaries and their summed per-test time, and the retry flag. The job gains `actions: read`, given only to the timings step, and the caller template grants it (§8). The `report` job runs `ctrf-io/github-test-reporter` v1.3.0, pinned by SHA, with no secrets and only `actions: read` and `contents: read`. Changes from ADR-011's configuration, recorded in ADR-018: the reporter reads only the first JSON file of an earlier run's artifact, so it saves its merged report as `main-watcher-report` and reads history from that, not from `main-watcher-ctrf`; and v1.3.0's insights table has no duration trend, so `previous-results-report` is added. v1.3.0 ranks the slowest tests by 95th percentile, not by average as ADR-011 expected (§12). `fail_upload` also deletes `timings.json`. Sandbox TS-S13, job-summary half, passed: over three runs of the 50 ms, 2 s and 20 s tests, the average, 95th percentile and run durations shown match xUnit v3's CTRF milliseconds (20.1 s, 2.1 s, 136 ms; runs 20.2 s, 20.3 s, 20.2 s), so R-17 does not occur with this pin; a flaky run was flagged as retried | Requester; platform team with Claude | ADR-018 (amends ADR-011) |
| 2026-09-16 | Lock issue open and close built (MainWatcher#10): a red result opens an App-authored `main-broken` issue (failing commit, tests, run link, `first_red`, `lease_until`, `reported_check`, `reported_sha`) before the check run completes; a green result closes open App locks. Mentions follow CQ-6; with nobody to mention a `watcher-infra` alert is raised. `watch.yml` alerts use the watcher repo's `GITHUB_TOKEN` with `issues: write`, de-duplicated by title; the `main-watcher` App gains Issues: write on targets, as the §8 matrix already states. `lock_lease` is fixed at 4 h until #19. In the sandbox a real lock blocked an unlabelled PR and let a `fixes-main` PR merge (TS-S4), and the green run closed it (TS-S3, first part) | Platform team with Claude | ADR-004, ADR-012, ADR-013, ADR-014 (no change) |
| 2026-09-16 | Push list built (MainWatcher#11, FR-3): the Reporter walks `main` back up to 100 commits for the newest green check run and lists repository activity since the push that made that commit the head before its run started, or up to 2 min after for clock skew (so a rollback to it is still listed; without that push in the activity read, every push read is listed and flagged as possibly incomplete), with a commit count from the compare API. The force-push fallback finds the green check run through the `after` commits of the activity it reads; with no green run, the last 100 pushes; on a read failure, "push list unavailable" with a compare link, or with no green commit known, a link to the commits. The body gains `last_green`. A later failing run adds one comment with a `check=` marker that mentions nobody; refreshing the body waits for replay (#12). The walk-back length is written to the `watch.yml` job summary (ADR-003 verification). Sandbox TS-S2 passed: three pushes during a slow run gave one lock listing all three and exactly one further test, of the newest commit | Platform team with Claude | ADR-003, ADR-013, ADR-017 (no change) |
| 2026-09-17 | Report replay built (MainWatcher#12, ADR-013 point 4): before any write, the Reporter lists App-authored `main-broken` issues, every open one plus those in any state updated within `reconcile_lookback`, and skips each write its marker shows was made: `reported_check` in the body, `check=` in a comment. A later failing run now also points the body's `reported_check` and `reported_sha` at itself, after its comment, which carries `sha=`. A check already on a closed lock creates nothing; a red result for a commit the newest lock reported (in the body or a comment), when someone other than the App closed it (`closed_by`), creates nothing, and a failure on a different commit opens a lock linking to the overridden one. Of two open locks the newer is closed with `state_reason: duplicate`, `duplicate_issue_id` (the kept lock's database ID) and a `closed=duplicate` comment. Pending ADR-015's closure pass (#20), each `watch.yml` cycle posts the ADR-004 comment on every lock closed by someone other than the App, naming who closed it and marked `closed=override`, after planning, even where the App had already commented `closed=green` or `closed=duplicate`. `MW_SANDBOX_EXIT_AFTER` exits after a named write for TS-S14. (MainWatcher#20 added ADR-015's
closure pass; the override comment still has its own step, after planning.) Sandbox TS-S14 (a), (b), (d) and TS-S3's override part passed; the run found that a replayed create skipped the "mention nobody" alert, now raised on replay | Platform team with Claude | ADR-004, ADR-013, ADR-015 (no change) |
| 2026-09-17 | Neutral results built (MainWatcher#13, CQ-5, ADR-013 point 1): the Reporter tells the neutral rows apart and raises a de-duplicated `watcher-infra` alert before completing the check run as `neutral`: "Outcome unknown" for a deleted run, "Outcome contract broken" for duplicate or missing names, and "Infrastructure error" when `main-watcher-tests-finished` is missing or not `success`; the contract and infrastructure alerts list the job's step conclusions, as does the check run output, whose title records the kind. A neutral result never touches a lock. An infrastructure error after a check run titled "Infrastructure error" also raises "Infrastructure errors twice in a row", the §11 signal, from the Reporter rather than the Planner. These alerts are required writes, as ADR-013's write order says: a failed alert leaves the check run `in_progress`, and the replay skips alerts that already carry the check's `check=` marker. Sandbox TS-S16 (a) to (f) passed: a failed upload stayed red with "failing tests unknown"; a restore failure was neutral with an alert, twice in a row with the second alert; a renamed test step gave "outcome contract broken"; a hung upload and a cancel during it stayed red, a cancel during the test step was neutral; the wrapper's 2-min deadline and a 1-min step `timeout-minutes` both left the marker `skipped` and gave neutral; a `report` job with no runner did not delay the lock. Stale-run cancellation, TS-S16 (g) and (h), remains #18 | Platform team with Claude | ADR-013, ADR-017 (no change) |
| 2026-09-16 | Dispatch diagnostics (MainWatcher#39): when a dispatch of `main-watcher-tests.yml` returns no run, `watch.yml` logs why: the HTTP status of a 5xx, the network error or timeout message, or a response without `workflow_run_id`. Dispatch recovery is unchanged: the POST is never retried, the check stays pending, and a 4xx still completes it as neutral. `docs/watcher.md` describes the log line | Platform team with Claude | ADR-010, ADR-017 (no change) |
| 2026-09-17 | Trigger worker built (MainWatcher#14): `src/MainWatcher.Worker`, a .NET 10 `BackgroundService` that every `check_period` reads each target through `mw-observer` and starts `watch.yml` through `mw-doorbell`, at most once per target per cycle. It flags an eligible head through the shared `Eligibility` rule (fixtures shared with the Planner's tests) and a finished `main-watcher` job through the Reporter's own outcome reader, and skips a target whose `watch.yml` run is still queued or running, read from the run name (§5.1, §9). Both Apps use installation tokens scoped to one repository, minted from their App JWT through `GitHubGateway`. `/healthz` reports liveness for the probe; configuration errors exit with code 2, including an App credential that GitHub rejects when the worker verifies both Apps before its first cycle. `deploy/worker` holds the manifests and the sandbox overlay; the worker's CI job builds the image and smoke-tests it. The worker's .NET version `[assumption]` is resolved (§13). In the sandbox, the deployed worker drove TS-S1 (11 idle minutes, 12 cycles, no runs) and TS-S2 (three pushes during a 4-minute run: one further test, of the newest commit, and a lock listing all three) with no hand-run cycle. Stale-run cancellation, lease renewal, reconciliation, queue sweeps and the worker's own alerts stay in #15 to #21 | Platform team with Claude | ADR-010, ADR-013, ADR-017 (no change) |
| 2026-09-17 | Worker health alerts built (MainWatcher#15, ADR-012, ADR-013 point 6): after each cycle the worker judges its own health and raises de-duplicated `watcher-infra` issues in the watcher repo through `mw-doorbell`. The conditions are three failing cycles in a row (the cycle threw, or any target errored), `watch.yml` runs started with none completed for 2 h, an installation token GitHub answers 401, 403 or 404 to, less than 20% of a rate-limit budget left (R-13), and a report owed for more than 15 min. Each is raised when it starts to hold and at most once an hour while it goes on, so a lasting fault is one thread; a condition that clears is forgotten. No alert is a required write: a failed one is logged and judged again next cycle, because the alert channel is usually what is failing. Reporting pending is timed from the `main-watcher` job's own `completed_at`, so the clock survives a restart, and such a check run is pending, never stale. Every target still owing a report is judged on every cycle, even one the cycle skipped because its own `watch.yml` run is queued, which is exactly when reporting is slowest; it stops owing one when a cycle looks at it and finds nothing owed, or when it leaves `targets.yml`. The "no run completed" clock is set from the newest completed run's own finishing time, not from the cycle that read it, because the same finished run is listed again for the next two hours. An idle watcher never alerts about completing no runs. Every response's `x-ratelimit-*` headers are read across both Apps (§11). Sandbox TS-S14 (c) passed: with the replica's App token narrowed to Issues: read, the Reporter answered 403 for 20 minutes and 17 `watch.yml` runs, the check run stayed `in_progress`, the alert was raised 15 minutes after the test job's `completed_at` and not after the check run started, and the lock was written on the first cycle after the permission was restored, with no duplicate lock or comment and no retest of the head meanwhile. A worker restart while the condition held commented on the open alert rather than opening a second issue, which is TS-U7 in the sandbox | Platform team with Claude | ADR-010, ADR-012, ADR-013 (no change) |
| 2026-09-17 | Hourly backup sweep built (MainWatcher#16, C-7, ADR-010): `watch.yml` gains `schedule: '17 * * * *'` and an optional `target`. A run with no target is a sweep: a first job lists the enabled targets with the same parser the cycles use, and the cycle job runs once for each as a matrix, capped at five, with the per-target concurrency group moved onto that job so a sweep's cycle never runs beside a dispatched one; the run is named `sweep`, so the worker does not read it as a target's cycle. `WorkFinder` moves into the shared library and dates each kind of work — the test job's completion, the check run's creation, the end of the dispatch window, and an eligible head's push or the moment `poll_interval` expired — so a sweep can say how long work waited. Work older than 15 min raises "trigger worker appears down" (§11), unless GitHub does not date it or the worker dispatched a cycle for that target within those 15 min, which the sweep reads from the watcher repo's own runs with a new `actions: read`: the worker is then alive and its "reporting pending" alert covers what is stuck. After the cycle the sweep alerts for `main-watcher/gate-fail-open` check runs posted in the past hour, read from the target's merge-group gate runs: the job is `needs: gate`, so runs created up to an hour earlier are read too and each job is judged by its own start time, making each sweep's window abut the last one's (ADR-008 point 3). Neither alert is a required write, and neither blocks the cycle's own reporting and testing, which is what the sweep exists to do when the worker is down. In the sandbox, with the worker scaled to zero, a sweep tested a push that had waited 121 min and raised the alert dated from the push itself, and a merge group whose gate met an expired lease was reported once and not again. C-7 was measured rather than assumed in the same test: GitHub dropped the cron's first two slots, 22:17Z and 23:17Z, while the workflow sat active on the default branch and hand-dispatched runs started normally, and ran the third at 00:19:03Z, two minutes late. A sweep-only design would have left that `main` untested for three hours | Platform team with Claude | ADR-008, ADR-010, ADR-013, ADR-017 (no change) |
| 2026-09-17 | Neutral retries finished (MainWatcher#17, ADR-017): the shared `Eligibility` rule already kept a neutral head eligible and capped it at three neutral results, and `watch.yml` already took `force`. What was missing is the signal when the cap is reached. `Eligibility.Capped` names that state — the head's newest check run is `neutral` and it has three of them — and is true from the moment the capping neutral is written, before the wait that would otherwise make the head eligible again, so the Planner raises "Head untestable on `owner/repo`" on the same cycle. The alert names the commit and every open App lock, because such a lock can no longer close on its own (R-23), and carries an `untestable sha=` marker, so later cycles say nothing more about that head while a head that runs out later comments on the same alert. A forced dispatch raises nothing: whoever sent it is already dealing with the head. It is the Planner's only write when it starts nothing and never blocks: a failure is logged and the cycle exits non-zero. Sandbox TS-S12 and TS-S18 passed with the trigger worker left running, so the worker and the Planner were tested on one rule: a target run cancelled inside the test step gave `neutral` with an alert and the same commit was tested again and passed; three restore failures on one head gave the alert two seconds after the third neutral, after which the worker dispatched nothing for 11 minutes and a hand cycle logged "No eligible head." with no repeat comment; a `force: true` dispatch started a fourth test; and a cycle stopped at the neutral write itself still led to a retest. `MW_SANDBOX_EXIT_AFTER` gained that boundary: the Reporter's check-run completion now goes through the same named-write hook its issue writes use, as `check:success`, `check:failure` or `check:neutral`, so a cycle can be killed with the neutral written and the Planner not yet reached (PR #48 review). A cancel that lands while the test step is tearing down after the tests finished leaves the marker `success` and the step `cancelled`, which the ADR-013 table reads as "outcome contract broken" — still neutral, still alerted, still retested | Platform team with Claude | ADR-013, ADR-017 (no change) |
| 2026-09-18 | Stale target runs built (MainWatcher#18, ADR-013 point 5): `StaleRun` holds the one deadline rule the Planner and the trigger worker both read. The queue deadline runs from the check run's creation until the job **starts**, judged on the job's status; the run deadline from the job's own `started_at` plus the `timeout` the run was **dispatched** with, the 20 minutes `run-integration-tests.yml` adds and a 10-minute grace. The jobs API does not report a job's `timeout-minutes`, so the Planner records the dispatched `timeout` in the check run's output, which the create call carries at no extra request, and every later output write carries it forward; reading `targets.yml` as it stands later would move the deadline of a job already running, cancelling a healthy job early when a target's `timeout` is lowered (PR #49 review, measured in the sandbox afterwards). Past either, `Planner.Stop` takes one step per cycle, each written to the check run's output **before** the request it describes: `cancel_requested` and a cancel, the same cancel again while the run lives, `force_cancel_requested` and a force-cancel 15 minutes on, then the alert "target run could not be stopped" and a force-cancel every cycle. The check run stays `in_progress` throughout, which is what stops a retry, a newer head and a forced dispatch alike (R-24); once the job completes, the outcome table judges it, and a deleted run gives "outcome unknown". Hidden markers moved out of the Reporter into `Markers`, since the check run's output now carries them too. In the sandbox, TS-S16 (g) and (h) passed with the worker running throughout: a job that started 7 s after its check run was untouched while it ran to 1.7 × the (shortened) queue deadline; a job on a runner label no runner has was cancelled at the deadline and gave `neutral`; a hanging job was cancelled at its run deadline and still opened a lock from its finished test step; and with every cancel refused, the alert was raised 30 minutes after the deadline and no test ran on a newer head for 78 minutes, a forced dispatch included, until the run was deleted. Two measurements: GitHub fills in `started_at` on a job that is still queued, which is why the rule reads the status; and `DELETE` on a still-running run answers `403`, so ADR-013's "a person can delete the run instead" is a two-step action and its wording needs an amending ADR | Platform team with Claude | ADR-010, ADR-013, ADR-017 (no change; ADR-013's delete wording is left for an amending ADR) |
| 2026-09-18 | Lock lease renewal built (MainWatcher#19, ADR-014): `Lease` holds the one rule the Planner, which renews, and the trigger worker, which asks for a renewal, both read; the gate keeps its own copy, since it shares no code. Every cycle that processes a target sets each open App lock's `lease_until` to now + `lock_lease`, before planning, so a watcher that cannot start a test still keeps its lock enforced while one that has stopped renews nothing. Renewing a lease that had already run out writes `lapsed=<expiry>..<renewal>` and the ADR-016 `sweep_required` in the same issue update, then comments, raises the de-duplicated "Lock lease lapsed" alert and records `lapse_reported`, so a cycle stopped between any two of those resumes without repeating a write; each write has a name the sandbox switch can stop after. A renewal keeps the windows still owed a report and appends its own, so a crash followed by a second lapse loses neither, and a cycle also reports the windows owed by locks that have since closed, since a green run closes one before the renewal runs. The marker is bounded at 20 entries by coalescing the two oldest into one span that says how many lapses it stands for, never by dropping one, so no obligation is lost; what each report says about what happened, what it allowed and what is true now is kept separate, so a coalesced span and a closed lock are described as they are (PR #52 review). The worker flags a lock whose lease is an hour old, or halfway through `lock_lease` where that comes sooner, which is the Issues: read `mw-observer` was given, and `MW_BOT_LOGIN` names the App to it. The half was the sandbox's finding: a fixed hour, against the ten-minute sandbox lease, asked for the renewal only once the lease had expired, so every renewal followed a real window in which the gate had stopped enforcing a lock nobody had abandoned. It only bites below a two-hour lease. `lock_lease` is one `targets.yml` setting outside the target list, 1 to 1440 minutes, because it bounds the watcher's own outage rather than any one repository. Sandbox TS-S7 passed with a 10-minute lease; its last clause, reporting the merge made during the lapse, waits on reconciliation (#20) | Platform team with Claude | ADR-014 (no change) |
| 2026-09-18 | Reconciliation built (MainWatcher#20, ADR-008, ADR-015): `Reconciliation` holds the rules — which activity types are merges, which pull request a commit subject names, whether `fixes-main` was on at `merged_at`, and the markers — and `Planner.Reconcile` runs them after planning on every cycle, because reporting must never hold testing back and this is the cycle's most expensive read. It processes every App lock that is open, or in any state and updated within `reconcile_lookback`, whose marker is not `reconciled=complete`, so a lock closed by hand before a merge was looked at is still followed through its closure; one activity read per target serves them all. Each unlabelled merge gets a comment (only once the lock has closed, so participants are notified), a de-duplicated "Merged while locked" alert and a body row under that heading, each carrying the same hidden key, and `last_reconciled` and `reconciled=complete` are written once at the end, so a pass that stopped part-way repeats no write and claims no completeness. A label history that cannot be read stops the pass at that merge rather than waving it through. **Attribution is by commit subject**, the two shapes the gate matches, because the commits-to-pull-requests API needs a Pull requests permission the App does not hold and ADR-008 promised no new one; a merge no subject attributes, or a range GitHub can no longer compare, is reported as the commit it left on `main` rather than passed over, which keeps NFR-4 honest at the cost of a report a rebase-merge target would find noisy. A lock left unreconciled 24 h after closing, or a merge left unjudged that long, raises "Reconciliation failing", keyed by the day. The worker flags a closed lock that is not complete, which is the only thing that would ask for a cycle once a lock is closed, at the cost of a second Issues read per idle target per cycle (R-13). The unlock comment now lists the pull requests the gate removed from the queue while the lock was open, read from the failed `merge_group` gate runs and named by each one's queue branch (R-7). Sandbox TS-S9 and TS-S15 passed, with TS-S7's outstanding clause discharged on the way: a first pass reconciled all sixteen closed sandbox locks from one activity read and reported the two unlabelled merges among them, while the three merges of lock #3 stayed unreported because their pull requests had carried `fixes-main` before merging — the first confirmation that a **pull request's label history is readable with the App's Issues permission alone**, which is what ADR-008 promised and what the whole pass rests on. The gate failed open on a 401 (TS-S9) and, in a second round, on an expired lease, and both merges were reported after a human had closed the lock first, one of them labelled `fixes-main` twelve seconds after it merged. One measurement changed the code: the merge queue writes an entry's merge commit when the entry starts its checks and merges it when they pass, so the commit date ran fifty seconds behind `merged_at`, and judging the labels at the commit date would report a pull request whose label went on while it waited in the queue. The pull request's own `merged` event turned out to be in the same `/issues/{n}/events` timeline as its label events, so the right moment costs no extra request and no extra permission. The PR #55 review found three ways a report could be lost or delayed, each now covered by a regression test: the cursor moved between two activity entries stamped in the **same second**, and since it is read back with a strict `>`, the entry left unjudged fell behind it for good and the lock was then marked complete, so the cursor now moves a whole second at a time; the compare read stopped at its first page of commits, which drops the **last** commits of a long range — exactly the ones that name the later pull requests — so the range is now read to its end and a range longer than 500 commits is reported as one that cannot be fully named, beside the pull requests it did name; and the "reconciliation failing" alert ran only after the reports had been written, so a lock whose writes keep failing — a locked conversation — was the one case nothing would ever report, and the overdue check now runs on it before the failure is passed on. The follow-up review rejected the first answer to the second of those, which reported an over-long range as one that could not be fully named and then let the lock be marked complete: `reconciled=complete` is a claim that every merge in the window was checked per pull request, and a generic commit-level warning does not make it true (ADR-015 point 1). A range longer than one pass now **keeps its entry in front of the cursor**, with `reconciled_commits` recording how far the pass got, and the next cycle carries on from that commit: what is bounded is the work one cycle does, not what is checked. A third round found where those two rules meet: with progress kept for only the one entry that truncated, two long ranges stamped in the **same second** livelocked, because the cursor cannot leave a second until all of it is done, so each pass restarted the range whose progress the other had overwritten and neither ever finished. The marker now holds every entry of that second, the finished ones as `done`, and is cleared once the second is behind the cursor | Platform team with Claude | ADR-008, ADR-015 (no change) |
| 2026-09-18 | Queue sweep built (MainWatcher#21, ADR-016): `QueueSweep` holds the rule the Planner, which sweeps, and the trigger worker, which asks for the cycles, both read. The obligation is a generation rather than a flag — `sweep_required` written by the same call that creates the lock or renews a lapsed lease, `queue_swept` only once nothing is left — so a crash anywhere between them leaves the debt visible and a second lapse's later generation covers the older one's runs. Each cycle lists the groups still queued from their `gh-readonly-queue/main/*` branches, reads the gate runs on each group's commit, and re-runs the ones that passed before the generation plus five minutes; a run still going is re-run once it completes, and one GitHub refuses to re-run leaves the sweep owed rather than passing the group. A re-run attempt's own start is what the next cycle reads, so one sweep never asks twice. The worker flags an owed sweep as work for any open lock and raises "queue sweep unfinished" after 15 min, through the timing it now shares with the pending-report alert. The unlock comment names the groups the sweep removed: a re-run fails on an attempt GitHub still dates the run by its first, so the gate-run read reaches a day further back and judges the window on the failing attempt. Sandbox TS-S17 passed in six rounds (`sandbox/issue-21-validation.md`), **confirming A-7 through the App and the watcher's own sweep**: a group whose gate had passed was removed 39 s after the lock opened, four minutes before its slow second check would have let it merge; a gate still running when the lapsed lease was renewed was left owed and re-run once it completed; a cycle stopped right after a renewal, with an older `queue_swept` on the issue, was swept by the next cycle (TS-S17 (b)); and a group that merged before anything could re-run it was reported by reconciliation. Two measurements changed the code. GitHub's issue list did not hold a lock created a second earlier, so the cycle that opened one swept nothing and the group stayed free to merge for two minutes; the Reporter now hands the locks it created to the sweep, which cut that to 39 s. And the gate required the issue body to hold exactly one marker, which stopped being true when reconciliation began writing a "Merged while locked" row per report (#20): from the first merge reported on a lock, the gate read no lease from it and failed open for the rest of that lock's life — the state where a lock matters most. `LockLease.ReadLeaseUntil` now reads `lease_until` across every marker and still refuses two of them. The PR #56 review found the worker's alert could be suppressed for good: only one reason can be the reason a cycle is dispatched, a report owed is found first, and the sweep debt was recorded only when it won, so a target whose reports kept failing had its unfinished sweep hidden for as long as that lasted — and the alert's own tracking then cleared it, because the target had been examined. `WorkFinder` now returns the dispatch reason **and** the debts the worker times, reading the open locks every cycle rather than only when nothing else asked for one, so silence about a sweep means a cycle looked and found it finished | Platform team with Claude | ADR-014, ADR-015, ADR-016 (no change) |
| 2026-09-18 | TS-S10 measured in the sandbox (MainWatcher#23, R-10, CQ-6): an App's team @-mention notifies the team only where the App holds organisation `Members: read`. Without it GitHub still resolves the handle — the lock body carries a real `team-mention` link, with `data-permission-text="Team members are private"` — but no member is notified and no thread subscription is created; two locks written that way notified nobody, while three locks naming a user individually, from the same App and repository on the same day, all did. With the permission granted, the next lock notified with reason `team_mention`, through the `notify` list and through the CODEOWNERS `*` fallback alike. So R-10 closes as an install requirement rather than a change to the Reporter: the fallback it offered, expanding a team handle to member logins, would have needed the same permission. The mention path itself was unchanged throughout, and no "mention nobody" alert was raised in either half | Platform team with Claude | ADR-004, ADR-012 (no change) |
| 2026-09-21 | Check-run timing section built (MainWatcher#22, FR-6, ADR-011): on a green or red result the Reporter adds suite wall-clock and summed time, the change from the last green run, the run's 5 slowest tests and a retry flag to the check run output, ahead of the lock details so the output cap cannot cut it off. `CtrfReader` now takes the timing from the reports it already validates: wall clock from the earliest summary `start` to the latest `stop`, summed time from the tests' `duration`, a test counted as retried when its `retries` is above zero. The conversion from milliseconds is the watcher's own (R-17). The last green time needs no new state and no artifact download: each check run's output carries its wall-clock time in a hidden `suite_ms` marker, read from the newest earlier green check run; a green run with no marker, or check runs that cannot be read, leave the change unknown and never hold the report back. The ADR's "slowest across runs" rule stays with the job summary: the check run lists this run's slowest, as ADR-011 point 3 specifies. Sandbox TS-S13, check-run half, passed (`sandbox/issue-22-validation.md`), so TS-S13 has now passed as a whole: two runs of the 50 ms, 2 s and 20 s tests showed exactly the CTRF artifact's wall-clock, summed and per-test values, the second run the change from the first and the retry flag for its flaky test. It found one fault: the first build truncated to the tenth, showing 20091 ms as 20.0 s beside the job summary's 20.1 s, so durations are now rounded | Platform team with Claude | ADR-011 (no change) |
| 2026-09-21 | Credential scope and the review checklist (MainWatcher#24, TS-S8): each TS-001 §6 checklist item except the App installations is now a watcher CI test (`SecurityChecklistTests`). They all pass, and each of the six that read files fails against a deliberately broken copy of its file. The sample target gained an `inspect_environment` switch. With it on, a sandbox test run searched its environment variables and the runner's work, temp and home folders, and found no private key, GitHub token or persisted git credential. TS-S8 passed, run with `sandbox/ts-s8-credential-scope.sh`. Its first run found `mw-observer` installed on `sample-target-slow`, which no `targets.yml` lists (R-11), and the App was removed from it. The first run also found that on a public repository GitHub validates a new issue's body before it checks the caller's permission, so issue writes are now probed with an empty update to an existing issue. The second run passed every check. The PR #59 review then found that the Secret check asked only `get` and treated a failed query as a denial, and that `main-watcher`'s installation went unchecked. The script now asks `get`, `list` and `watch`, and fails on anything but an explicit `no`. It also reads `main-watcher`'s installation through a new metadata-only workflow, `app-installations.yml`, in the `reporter` environment. The third run passed the `main-watcher` check. It failed the Secret check, because `kubectl auth can-i --as=system:anonymous` asks as the anonymous user, which may not ask. So the earlier runs' anonymous `get` row had passed on an error, and the check now uses `SubjectAccessReview`s made by the operator. **TS-S8 passed** on the fourth run, all 35 checks: 12 access reviews answered `allowed: false`, and all three Apps are installed exactly where R-11 allows. A follow-up review found that `allowed: false` can come with an `evaluationError`, so the check now counts a denial only when the evaluation error is empty. A fifth run passed all 35 checks with that rule, every evaluation error empty. The script covers 403 probes for `mw-observer` and `mw-doorbell` with working controls beside them; where the keys are; each App's installed repositories (R-11); who can read the worker's Secret; and inbound exposure. TS-001's TS-S8 row names both | Platform team with Claude | ADR-009, ADR-010 (no change) |
| 2026-09-21 | The scenario suite (MainWatcher#25, TS-001 §5): `sandbox/run-scenarios.sh` runs TS-S1 to TS-S18 against the sandbox from one entry point, as 25 units on a pool of ten targets, `sample-target` and `sample-target-2` to `-10`. Each unit has a target of its own and is followed by a reset of it. The units that need the watcher down share one outage: they open their locks while the worker runs; then the worker is scaled to zero and `watch.yml` disabled; once they are done, one restoring sweep is dispatched and awaited before the worker is started again. The TS-S16 (h) units start with them, since their wait for the run deadline needs no worker. The sandbox switches now take per-target entries (`owner/repo=value`, `SandboxSwitch`), so one target can be faulted while the others run; the queue deadline is shortened for `sample-target-10` alone, in the worker's overlay and the replica variable alike; and TS-S14 (c) gets a read-only Issues token for its target instead of a revoked permission. TS-S16 (c), (e), (f), (g) and (h) use workflow variants that `publish-public.sh` publishes as branches of the public gate repo. **Releases are gated.** A full passing run posts the commit status `scenario-suite`; `release.yml` moves the workflow tag, or builds and pushes the worker image, only for a commit on `main` whose newest `scenario-suite` status is `success`. Any run that includes TS-S13 posts `scenario-suite/ts-s13`, which `ci.yml`'s `reporter-pin` job requires on a pull request that changes the reporter pin. `v*` tags are protected by a ruleset whose only bypass is a release App: GitHub refused GitHub Actions' own token as a bypass actor, and the organisation allows no deploy keys (`docs/release.md`). **The suite cannot take the 30 min TS-001 planned.** TS-S16 (h)'s unstoppable run needs about 90 min of GitHub time on its own, from its run deadline through the refused cancel and force-cancel to the alert. One full run on ten targets took 100 min. **No full run has passed yet.** The suite needs about twice the sandbox `main-watcher` installation's API budget, 5000 requests an hour shared by every target's cycles, so the release status waits on MainWatcher#60. That measured it: ten targets spent the budget in 39 min, and six targets ran at about 10,000 an hour. Every cycle then failed with a bare 403 and no alert, since R-13 watches only the worker's own Apps. The gateway now names a refused request with GitHub's message and rate-limit headers, and each cycle logs its request count by endpoint and the budget it left. The suite runs six targets by default. **What the runs found** (`sandbox/issue-25-validation.md`), in Main Watcher: (1) TS-S15's recovering cycle reported two merges made during a lock 2 s apart, and opened two "Merged while locked" alerts instead of one alert and a comment, because the issue list did not yet hold the first. That is the gap TS-S17 found for lock issues, and `Alerts` now remembers the alerts it opened for 2 minutes. (2) TS-S14: a lock closed by hand between a cycle's override check and its reconciliation was marked `reconciled=complete`, so the worker saw no work, and the ADR-004 override comment waited for an unrelated cycle. Reconciliation now completes only the closed locks the same cycle's override check saw closed. (3) The override check read the comments of every closed lock in the 30-day lookback, and asked who closed it, on every cycle, a cost that grows with every lock. It now skips complete locks, which with (2) are ones whose closure has been judged. In the suite: a `watch.yml` dispatched a second after the workflow was enabled was accepted and never queued, and could not be cancelled; TS-S8 probed issue writes on a new target with no issues; and a unit left a 2-minute timeout in `targets.yml` that then refused the next head's caller. The sandbox's watcher replica was made public: on the Free plan only private repos use the 2000 Actions minutes a month, and one full run used about 700 of them there. After the PR #61 review, the `report` job also keeps the markdown its reporter generated as the `main-watcher-summary` artifact, since the job summary is not in the REST API, and TS-S13 checks that summary's slowest tests and their units against the CTRF reports; before, it checked only that the `report` job succeeded | Platform team with Claude | ADR-004, ADR-012, ADR-015 (no change: de-duplicated alerts now hold when the issue list lags, and an override is noted before its lock's reconciliation completes); R-13 understated, MainWatcher#60 |
| 2026-09-22 | The `main-watcher` installation's API budget (MainWatcher#60, R-13): every target's `watch.yml` cycles share that one installation's 5000 requests an hour, and the scenario suite spent it with no alert, because R-13 watched only the worker's own Apps. **Measured:** each cycle's new per-endpoint log (`sandbox/issue-60-validation.md`) showed a test-dispatch cycle costing 208 to 211 requests and a report cycle 212 to 215, of which 193 to 197 were one check-runs read per commit on `main`: `GitHubGateway.Checks` read every commit's check runs on its first call, and the watcher, a new process each cycle, always made a first call. That cost grew with every push, and on a target with a long history would exceed the hour's budget in one cycle. A first read now walks back from the head only to the last green run, or to 50 commits (`ChecksLimit`). Only the Planner creates check runs, on the head, and never while one is pending (ADR-017), so a pending run is always the newest, and finding the newest finds it however many pushes followed; what the limit leaves out is only a green run further back, which only the timing comparison uses. When those commits hold no check run at all, the rest of the history is searched with **no** limit, through GraphQL, 100 commits a request: its budget is separate from the REST one the cycles spend, and a page costs 2 of its 5000 points an hour. The PR #62 review rejected a cap of 50 and then of 1000 commits, each of which could hide the pending run the search exists to find. It brought a cycle to 17 requests for a test dispatch and 21 for a report, the medians of 209 cycles in the next full suite run (run 9, 2026-09-22). **Run 10, on the commit that merged this work, was the suite's first full pass:** all 25 units in 115 minutes, `scenario-suite` = `success` on `1e68709`, and 208 cycles spending 4614 requests, about 2,400 an hour, none refused and never less than 3113 of the 5000 left. That run sent about 2,400 requests an hour on six targets, never had one refused, and never left less than 3016 of the 5000. Its two failures were in the suite: TS-S15 read a merging pull request in the second between leaving the queue and merging, and TS-S8 still probed the replica, public since #25, with an empty new issue and expected the Apps on only the run's targets. Both then passed on their own. **Alerts.** The gateway keeps the lowest budget it saw in the current window, rather than the latest, since GitHub answered one sweep from two budgets refilling seconds apart, and records the first request refused on a primary or secondary rate limit, wherever the cycle caught it. After every cycle, including one that failed, `InstallationBudget` raises "The main-watcher App's API rate limit is refusing cycles", quoting the refusal, or "The main-watcher App's API budget is below 20%", each keyed by the refill minute so a lasting shortage is one comment an hour however many targets see it. Because cycles for different targets and a sweep's legs raise them at the same moment, each claims the window before writing it, by creating a label whose name is a digest of the alert's title and window and of nothing else: a label name is unique in a repository, so GitHub lets exactly one cycle create it and only that one writes, and no duplicate is ever notified. The PR #62 review rejected writing first and tidying up afterwards, since the notifications have gone out by then, and then a claim name that also carried the claiming cycle's minute, which two cycles either side of a minute boundary did not share. A claim whose write fails is given back, and claims older than a day, by the time in their description, are deleted by the next winner. A secondary limit is also recognised by its message, since GitHub documents its `retry-after` as optional. They go through the workflow's `GITHUB_TOKEN`, a separate budget, so they can be written when the App's is spent. A failed budget alert fails the run. ETags were considered and deferred: after the fix a cycle's reads are few, and most change between cycles | Platform team with Claude | ADR-012, ADR-017 (no change); R-13 rewritten |
| 2026-09-22 | Onboarding guide and dry run (MainWatcher#26, FR-1): `docs/onboarding.md` is the working procedure, and §10's list above is reordered to match it — the Apps and the two copied workflows first, then an entry added with `enabled: false`, then the dry run, and only then the required gate check and `enabled: true`. Nothing is enforced until the ruleset changes, and no cycle runs until the entry is enabled, so a half-finished onboarding cannot block a repo's merges. **The dry run** (`dry-run.yml`, `DryRun`) checks, in order and stopping at the first failure: what the parsed entry means; that the target's caller exists on `main` and its three execution settings are the entry's, through the check the Planner makes before every dispatch; that the gate workflow exists and runs the gate action; that the head of `main` dispatched produced exactly one discoverable run, found by the caller's `run-name` as recovery finds one (R-14); that run's ADR-013 outcome; and that its `main-watcher-ctrf` artifact validates against the CTRF schema, through the reader every cycle uses (ADR-007). It creates **no check run**, which is what a red result would need to become a lock, and its App token asks only for Contents: read and Actions: write, so nothing it holds can write a check run or an issue. It accepts an entry that is still `enabled: false` — the state an entry has while it is being onboarded — and shares the target's `watch.yml` concurrency group, so a dry run and a cycle never test one repo at once. A red suite passes it: the contract is what is tested, and a failing test exercises more of it than a passing one; the outcome line says so, and says a real cycle would open the lock for that commit. Whether the gate is a **required** check is in a ruleset the watcher cannot read, which is why TS-S5 at onboarding is the step that confirms it (A-5). **Entries are validated in CI:** `ci.yml`'s `targets` job runs the cycles' own parser over the committed file through `--check-targets` and prints what each entry was understood to mean, so a malformed entry fails the onboarding pull request rather than the next hourly sweep. **Rollback** is written down in one place (`docs/onboarding.md`): `enabled: false` for one target, the previous image tag for the worker, a revert or a tag move for the workflows — with the consequence that disabling a target does not close an open lock, whose lease then lapses within `lock_lease` and lets merges through on their own (ADR-014). TS-U16 added. Writing the guide also found that **OIDC, which §8 recommends, is not reachable**: the reusable workflow's `main-watcher` job declares its own `permissions`, which cap what its steps get whatever the caller grants, so `id-token: write` never arrives. Granting it is a breaking change — GitHub fails a run whose called job asks for a permission its caller did not grant, so the workflow and every caller would have to change in one release — and so is recorded as an open item in §8 rather than smuggled into this one. No target has asked for OIDC yet | Platform team with Claude | ADR-002, ADR-007, ADR-009, ADR-013, ADR-014 (no change) |
| 2026-09-22 | The PR #64 review, all three findings taken. (1) **TS-S5 at onboarding needs a lock to exist**: the gate passes every merge group while none is open, so the guide's step would have confirmed nothing. The hand-made lock workflow is no longer sandbox-only, and is renamed `sandbox-lock.yml` → `lock.yml` to say so (the suite dispatches it by filename, so `sandbox/scenarios` moves with it; the #16 and #20 validation records keep the old name, which is what ran at the time) — it resolves a bare `target` to a `main-watcher-sandbox` repository as before, keeping the scenario suite's dispatches unchanged, and otherwise accepts an `owner/repo` **listed in `targets.yml`**, refusing anything else: a hand-made lock blocks a repository's merge queue, and those two are the repositories this watcher could lock through a cycle anyway. The guide now opens a lock with a one-hour lease, queues the mixed group, requires it to fail, and closes the lock as the App so no override is recorded; the lease is what bounds a lock someone forgets (ADR-014), and the step runs while the entry is still `enabled: false` so no cycle can interfere. TS-001's TS-S5 row says so. (2) **A completed run with no `main-watcher` job is judged at once.** The dry run had treated an empty jobs list as a job GitHub had not created yet, which `GitHubGateway.Jobs` already distinguishes — it answers a pending run with a *queued* job and returns empty only for a completed run — so a renamed caller job would have waited out the whole timeout and then blamed the timeout. (3) **The dry run cannot outlast its own App token.** The workflow mints one token, which lasts an hour, and the dry run cannot renew it: the key stays in the `reporter` environment where only the token action reads it (§8). A 340-minute target would have failed authentication part-way through judging and reported that instead of the setup. It now waits for the target's `timeout` plus 30 minutes or 50 minutes, whichever is sooner, and `dry-run.yml`'s job timeout comes down from 360 to 70 minutes to match. A cap alone would have left a slow but correct target unable ever to have its CTRF validated, which is the requirement, so the dry run is **resumable**: `run_id` (`MW_DRY_RUN_ID`) judges a target run that already exists instead of dispatching one, so a later job, separately authenticated, makes the outcome and schema checks the first could not reach and starts no second test. A wait stopped by the token says nothing is known to be wrong and prints that command. A resumed dry run makes every other check again, so its report stands alone; nothing verifies that the run given is a `main-watcher-tests` run, because the outcome and artifact checks do it by the contract's own names, and an unrelated run fails them saying so. The dry run was then exercised against the live sandbox target (`sandbox/issue-26-validation.md`): a correct target passed all six checks with the CTRF validated; an entry whose `timeout` disagreed with the caller stopped after one request and dispatched nothing; a red suite passed the dry run, named the failing test from the artifact, and — the point of the design — left **zero** `main-watcher` check runs and no issue on a commit a cycle would have locked; and a resumed run made every check again with five GETs, no dispatch, and the same suite time to the millisecond. It also reported while the target run was still `in_progress`, its `report` job unfinished, which is ADR-013 point 3 in practice. The same six checks then ran through `dry-run.yml` on the replica with the App token it mints, in 85 seconds, again leaving no check run and no issue; a resume through its `run_id` input judged that run with five GETs and left the target's count of test runs unchanged at 183, which is what "starts no second test" means in practice. The renamed `lock.yml` was exercised on both sides of its guard: a repository neither in the sandbox nor in `targets.yml` was refused **before** any App token was minted, and one listed in `targets.yml` was admitted by the guard and failed only at the token, which separates the two branches. **TS-S5 passed at onboarding**, run as the guide tells a team to run it: a hand-made App-authored lock with a one-hour lease, two pull requests queued back to back so the second group was built on the first and held both, the `fixes-main` one merged, the unlabelled one removed with the gate naming it, and the lock closed by the App so no override was recorded. TS-U16 covers (2) and (3), and the checklist test that the workflows holding the main App key run no target code now covers the lock workflow as well. CI's actionlint runs shellcheck, which a local actionlint without it does not: the lock workflow's three `>> "$GITHUB_OUTPUT"` redirects are now one (SC2129) | Platform team with Claude | ADR-013, ADR-014 (no change) |
| 2026-09-23 | Job steps not yet written down (MainWatcher#65, FR-4): scenario suite run 11 found a red `main` reported neutral, with no lock, because the Reporter read the `main-watcher` job seconds after it completed, when GitHub listed only 6 of its 16 steps and no marker. `sandbox/run-scenarios.sh capture-jobs` then read the jobs API every second around completion (`sandbox/issue-65-validation.md`): a force-cancelled job read 1 s after `completed_at` was `completed` with ten steps pending and no `Complete job` step, and final 4 s later; every final list, cancelled ones included, had every step concluded and `Complete job` last; a job cancelled before a runner has no steps. ADR-019: where the table would say the tests did not finish, a job whose steps are not final is not judged for up to 5 minutes after it completed. The check run stays `in_progress`, the worker flags no report meanwhile, and the dry run waits it out; afterwards the job is judged as it stands, with a note. The gateway now reads step `status` and `completed_at` and the job's `conclusion`; the suite keeps the raw jobs responses it reads and names this regression when a red or green unit comes out neutral. TS-U5 and TS-U11 extended | Requester; platform team with Claude | ADR-019 (amends ADR-013) |
| 2026-09-23 | Stuck `watch.yml` runs (MainWatcher#67, FR-2): scenario suite run 11 found a `watch.yml` run for `sample-target-5` held in GitHub's `waiting` state for 25 minutes at the `reporter` environment's gate, which has no reviewers; its pending deployment listed none, with `current_user_can_approve: false`. The worker skips a target whose run is queued or running, so the target went untested and unreported until a person cancelled the run; "reporting pending" fired, but nothing cleared the blockage. ADR-020, decided one point at a time with the requester: the `reporter` environment must have no required reviewers (an install requirement); a `watch.yml` run for a target not `in_progress` 20 minutes after `created_at`, plus any wait timer, is alerted about and cancelled by the worker through `mw-doorbell`, force-cancelled 15 minutes later and alerted about again 15 minutes after that, with the next cycle dispatching again and no limit on repeats; a `waiting` run whose `pending_deployments` lists reviewers is only alerted about. Sweep runs are out of scope, and the ADR records what a stuck one costs. Dropping the environment for a repository secret was rejected, as it would widen the key's reach (§8). `mw-observer` can read `pending_deployments`: its installation token got 200 on the sandbox replica, and GitHub names `actions=read` as the permission needed. TS-U17 and TS-S19 planned; not yet built | Requester; platform team with Claude | ADR-020 (amends ADR-010, ADR-013) |
| 2026-09-23 | Stuck `watch.yml` runs built (MainWatcher#67, ADR-020): every cycle, the trigger worker judges its own unfinished `watch.yml` runs for targets, which it already read to skip active targets. One not `in_progress` 20 minutes after `created_at`, plus the environment's wait timer, is cancelled through `mw-doorbell` with the alert "`watch.yml` run stuck `<state>` on `owner/repo`", force-cancelled 15 minutes later, and alerted about again as "could not be stopped" 15 minutes after that, each step timed from GitHub's own times so a restart resumes it; once it completes, the target is dispatched again. A `waiting` run's `pending_deployments` is read through `mw-observer` first: a gate listing reviewers is left alone and named in "`watch.yml` run waiting for a reviewer on `owner/repo`", and a failed read cancels nothing and counts towards "cycles keep failing". Sweep runs are never touched. The PR #72 review found two gaps, both fixed: runs were read only from the last two hours, so a two-hour wait timer's 140-minute deadline was never reached and an unstoppable run stopped being force-cancelled at two hours, and runs not yet started are now also listed by status whatever their age, four requests a cycle; and a failed gate read cleared the target's alert, so the next read alerted again at once, which a target left unjudged now no longer does. The gateway gained `PendingDeployments`, reading each gate's wait timer and reviewers (a login, or `team <slug>`). "No required reviewers" on `reporter` is now in `docs/watcher.md` and `docs/onboarding.md`, and `docs/worker.md` describes the rule and its three alerts. TS-U17 (`StuckRunTests`) covers the states, the deadline, the wait timer, the escalation, reviewers, an unreadable gate, a refused cancel, sweeps, the re-dispatch and the alerts. **TS-S19 passed** (`sandbox/issue-67-validation.md`): the `reporter` environment is shared by every target, so a sandbox-only `MW_SANDBOX_REVIEWED_TARGETS` sends only its target's cycles to `reporter-reviewed`, which has the operator as required reviewer and no secrets. The cycle waited there, the worker read the reviewer through `mw-observer`, left the run alone on every cycle, and raised the reviewer alert 21 minutes after the run was created; once the run was cleared by hand, the next cycle tested and reported the head. The cancel path for a gate listing no reviewers cannot be produced on demand and rests on TS-U17 | Platform team with Claude | ADR-020 (no change) |
| 2026-09-23 | xUnit 4.x targets (MainWatcher#71, FR-3, FR-6, C-5): the production targets use xUnit 4.0.0. Run on the sandbox template, it rejects `--report-ctrf` (MTP 2 exits 5, no report), takes `--report-xunit-ctrf` instead, writes every project's report to the root `TestResults/`, and writes `suite` as an array, which the vendored schema rejected, so failing tests were "unknown", the timing section missing and the dry run failing. The schema's pin now accepts a string or an array of strings, the failure list shows `extra.type`, the test class, as the suite, and `sandbox/sample-target` runs 4.0.0. The job summary reporter's pin read 4.0.0 reports with the right units. The dry run failed on a red 4.0.0 run before the change and passes after it, and the full scenario suite passed on 4.0.0 targets, 26 of 26 units, TS-S13 included (`sandbox/issue-71-validation.md`) | Platform team with Claude | ADR-021 amends ADR-007 |
| 2026-09-23 | TS-S5 runs through `lock.yml` (MainWatcher#66): the scenario suite's helpers for hand-made locks had had no caller since the suite was built (#25), while TS-001 said TS-S5 used them, and the one unit covering TS-S4 and TS-S5 took a real lock from a failing push. TS-S5 is now a unit of its own that runs as the onboarding guide does: it sets the target's entry `enabled: false`, so no cycle closes the lock or reconciles against it, has `lock.yml` open an App-authored lock with a 1-hour lease, checks that a batched group mixing a `fixes-main` pull request and an unlabelled one fails the gate, and has `lock.yml` close the lock as the App. So every release run dispatches the workflow that holds the main App key and that onboarding tells teams to run, and TS-S5 no longer depends on a failing push. TS-S4 keeps its real lock. TS-001's TS-S5 row says so | Platform team with Claude | ADR-002, ADR-014 (no change) |
