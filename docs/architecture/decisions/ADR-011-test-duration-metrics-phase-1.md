---
id: ADR-011
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "someone needs cross-repo duration views, slowdown alerts, or history beyond artifact retention"
sources: [FR-6, ADR-007, ADR-009]
confidence: confirmed
---

# ADR-011 — Test-duration metrics, phase 1: per-run reports inside GitHub; own metrics store deferred

**Deciders:** requester, platform team · **Consulted:** —

## Context

New requirement FR-6 (2026-09-15): capture how long integration tests take, and identify
the slowest tests within a suite.

**The raw data already exists** because of ADR-007 and ADR-009:
- per-test durations and suite start/stop times in the CTRF report;
- step timings, and queue time, from the target's Actions run.

**What is missing** is storage and presentation. The organisation runs no metrics stack
(no Prometheus, Grafana or PostgreSQL). The requester chose to start inside GitHub and
decide on an own store later.

## Decision

**Phase 1** is delivered entirely inside the shared test workflow and the watcher. The
shared workflow `run-integration-tests.yml` does three things:

1. **Records timings.** After the test job it writes `timings.json` into the
   `main-watcher-ctrf` artifact, containing:
   - queue wait (run created → job started);
   - restore, build and test step durations;
   - test wall-clock time (from the CTRF summary);
   - the sum of per-test durations;
   - whether a retry happened;
   - `sha` and the run ID.

2. **Publishes a report.** A separate `report` job runs the CTRF GitHub Test Reporter
   action (`ctrf-io/github-test-reporter`, pinned by commit SHA). This job:
   - has no secret references;
   - has `permissions: actions: read, contents: read`;
   - enables `summary-report`, `slowest-report`, `insights-report` and
     `flaky-rate-report`;
   - uses `artifact-name: main-watcher-ctrf` and `previous-results-max: 100`.

   Each run's job summary then shows its results, duration trends, and the top 10 slowest
   tests by average across previous runs.

3. **Adds a timing section to the check run.** The Reporter in `watch.yml` adds to the
   target commit's check run output:
   - suite wall-clock time and its change from the previous green run;
   - the 5 slowest tests of this run;
   - a retry flag.

**Measurement rules**, applied in all three:
- report wall-clock time and summed per-test time separately (xUnit runs collections in
  parallel);
- flag retried runs;
- "slowest" means across runs, not within one run.

**Phase 2** (a store owned by the organisation, cross-repo dashboards, slowdown alerts) is
**deferred**. The recommended shape when it is needed: PostgreSQL + Grafana, with the
trigger worker ingesting `timings.json` and the CTRF reports. Raw per-test rows would be
kept about 90 days, with daily summaries kept indefinitely. `timings.json` is written from
day one, so phase 2 can backfill history as far back as artifact retention allows.

## Options considered

### Option A — GitHub-only reports *(chosen as phase 1)*

### Option B-lite — PostgreSQL + Grafana fed by the worker *(deferred)*

Gives long history, cross-repo views and alerts. Deferred because there is no existing
database or dashboard infrastructure to run it on, and no current need justifies adding
one.

### Option B-full — Prometheus + Grafana + PostgreSQL

Rejected for now. It adds three components when suite-level data is only one row per run.

## Consequences

**Positive**
- No new infrastructure.
- Developers see timing and the slowest tests in their own repo's Actions tab, on every
  run.
- Data needed for phase 2 is captured from the start.

**Negative**
- **Per-repo view only.** No cross-repo dashboard and no automatic alert when a suite
  slows down.
- **Limited history.** It stops at the artifact retention period and at the reporter's
  `previous-results-max`.
- **The reporter ranks by average,** not median or 95th percentile. That is acceptable for
  phase 1.
- **A third-party action runs in every target's workflow run** (R-16). Mitigated by the
  SHA pin, the secret-free job, and the minimal token.
- **Duration units are unverified.** The reporter has an open issue about durations shown
  in the wrong unit. It must be checked against xUnit v3's CTRF output in the sandbox
  (TS-S13).

**Follow-on work**
- Add the `timings.json` writer and the `report` job to the reusable workflow.
- Add the timing section to the check run output.
- Run the sandbox check of duration units.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Reusable workflow v1 `report` job and `timings.json`; Reporter check-run output | TS-S13, TS-U6 | Job summary visible on every run |

## Revisit when

Someone needs cross-repo duration dashboards, slowdown alerts, or history beyond artifact
retention. Then implement phase 2 (B-lite).
