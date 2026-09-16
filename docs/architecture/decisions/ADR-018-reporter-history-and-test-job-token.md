---
id: ADR-018
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-16
review_by: 2027-03-15
review_trigger: "the reporter pin is updated, the reporter reads more than one file from an earlier run's artifact, or the run start time becomes available without the Actions API"
sources: [FR-6, R-16, R-17, ADR-007, ADR-009, ADR-011, ADR-013]
confidence: confirmed
amends: ADR-011
---

# ADR-018 — The job summary keeps its own history artifact, and the test job may read Actions (amends ADR-011)

**Deciders:** requester (confirmed 2026-09-16, MainWatcher#8), platform team · **Consulted:** —

## Context

ADR-011 set out the phase 1 timings: `timings.json` in the `main-watcher-ctrf` artifact, and
a `report` job running `ctrf-io/github-test-reporter` with `artifact-name: main-watcher-ctrf`
and four reports (summary, slowest, insights, flaky rate). Building it (MainWatcher#8) against
reporter v1.3.0 and the sandbox found four places where that configuration does not do what
ADR-011 intended:

- **History is read from the wrong file.** For each earlier run, the reporter opens the
  artifact named by `artifact-name` and reads only the **first** `.json` entry in its zip.
  `main-watcher-ctrf` holds one CTRF report per test project plus `timings.json`, so the
  history would contain one project's tests, or a file that is not CTRF at all.
- **The run's start time is only in the API.** The queue wait in `timings.json` is measured
  from the run's start to the job's start. No expression or environment variable holds
  either; they come from the Actions runs and jobs endpoints, which need `actions: read` on a
  private repo. ADR-011 gave that permission to the `report` job only, but `timings.json` must
  be written by the `main-watcher` job, because the Reporter reads the artifact as soon as that
  job completes (ADR-013), before the `report` job may even have a runner.
- **No duration trend.** In v1.3.0 the insights table shows tests per run, flaky and failed
  totals, and the single slowest test. It shows no run duration over time.
- **Slowest means 95th percentile.** v1.3.0 ranks the slowest tests by p95 duration and shows
  the average beside it. ADR-011 expected a ranking by average.

## Decision

**1. The report job keeps its own history artifact.** The `report` job downloads
`main-watcher-ctrf`, merges every CTRF report in it, and runs the reporter with
`artifact-name: main-watcher-report` and `upload-artifact: true`. The reporter uploads its
merged report, a single `ctrf-report.json`, as `main-watcher-report`, and reads earlier runs'
history from that artifact. `main-watcher-ctrf` stays as ADR-007 and ADR-011 define it, and
remains the only artifact the Reporter reads.

**2. The `main-watcher` job has `actions: read`.** Its permissions are `contents: read` and
`actions: read`. Only the step that writes `timings.json` is given the token. The restore and
test steps, which run target code with the target's secrets, never receive it. The caller
template grants `actions: read`, which the `report` job needs anyway.

**3. The duration trend is the previous-results report.** The reporter also runs with
`previous-results-report: true`, which lists each earlier run with its wall-clock time. The
summary-delta report is not used: in v1.3.0 its template passes one value to a duration
formatter that expects two, so it shows wrong units.

**4. "Slowest" follows the reporter.** The job summary ranks the slowest tests by 95th
percentile across up to 100 runs, with the average shown. ADR-011's negative consequence
"the reporter ranks by average" no longer applies. The check run's timing section (ADR-011,
decision 3) is unchanged.

Everything else in ADR-011 stands: the `report` job has no secret references, requests only
`actions: read` and `contents: read`, and pins the reporter by commit SHA.

## Options considered

### Option A — A separate history artifact written by the reporter *(chosen)*

It gives correct history with no code of ours in the report path, and leaves
`main-watcher-ctrf` unchanged for the Reporter.

### Option B — Put one merged CTRF report first in `main-watcher-ctrf`

The `main-watcher` job would write a merged report so it becomes the first `.json` entry. It
lost because zip entry order depends on how `upload-artifact` walks the files, which is not
a documented contract, and a duplicate of every test in the artifact would confuse the
Reporter's own CTRF reading (ADR-007).

### Option C — Re-upload `main-watcher-ctrf` from the report job

It lost because the Reporter reads the artifact as soon as the `main-watcher` job completes
(ADR-013). Replacing it later races with that read, and a replayed report could see a
different artifact from the first attempt.

### Option D — Queue wait without `actions: read`

Measure it without the API, or leave it out. It lost because no runner variable or
expression gives the run's start time, and ADR-011 requires the queue wait. Computing it in
the `report` job instead would miss the artifact, as in option C.

## Consequences

**Positive**
- The job summary's history, slowest tests and flaky rates cover every test project.
- `main-watcher-ctrf`, the Reporter's input, is unchanged.
- The summary shows a real duration trend, in correct units (TS-S13, R-17).

**Negative**
- **A second artifact per run**, about 3 KB, kept for the target repo's artifact retention.
- **The test job's token can read Actions data** (runs, logs, artifacts) of the target repo.
  Only the timings step receives it, but it is a wider token than ADR-011 described for that
  job.
- **The reporter's behaviour is part of the contract.** A pin update can change how history is
  read, how "slowest" is ranked, or the report templates. TS-S13 runs on each pin update.
- **History starts empty.** Runs from before this change have no `main-watcher-report`, so the
  trend begins with the first run that has one.

**Follow-on work**
- None beyond ADR-011's: the check run's timing section, in the Reporter.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Reusable workflow `report` job (`main-watcher-report`, previous-results report); `main-watcher` job permissions; caller template | TS-S13 (job-summary half passed in the sandbox on 2026-09-16), TS-U6 | Job summary on every run; review of each reporter pin update |

## Revisit when

- The reporter pin is updated.
- The reporter reads every file of an earlier run's artifact, so `main-watcher-ctrf` could
  serve as history again.
- GitHub exposes the run's start time without the Actions API.
