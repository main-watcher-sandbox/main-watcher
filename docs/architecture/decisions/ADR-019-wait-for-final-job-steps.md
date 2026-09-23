---
id: ADR-019
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-23
review_by: 2027-03-15
review_trigger: "a completed job's steps take more than a minute to become final, a finalised job's steps do not end with `Complete job`, or GitHub changes how the jobs API lists steps"
sources: [FR-3, FR-4, ADR-010, ADR-013, ADR-017]
confidence: confirmed
amends: ADR-013
---

# ADR-019 — A completed job is judged only once GitHub has written its steps down (amends ADR-013)

**Deciders:** requester (confirmed 2026-09-23, MainWatcher#65), platform team · **Consulted:** —

## Context

ADR-013's outcome table judges a `main-watcher` job once the jobs API shows it `completed`. The
tests count only when the `main-watcher-tests-finished` marker step succeeded. When the marker is
missing or not `success`, the result is an infrastructure error: `neutral`, an alert, and no lock.

Run 11 of the scenario suite, on 2026-09-23, found that "completed" does not mean "written down"
(MainWatcher#65). A job whose tests had failed was read seconds after it completed. GitHub already
reported it `completed`, but listed only 6 of its 16 steps, half of them with no conclusion, and no
marker step. The Reporter reported an infrastructure error, and a genuinely red `main` did not lock
the merge queue. Read again later, the same job listed every step with its real conclusion.

A completed job whose later steps have no conclusion also looked like a cancelled or lost-runner
job, which must stay an infrastructure error (ADR-013 point 5, TS-S16). So the evidence came first.
`sandbox/run-scenarios.sh capture-jobs` read the jobs API every second around completion for three
failing runs, a cancel, a force-cancel and a cancel before a runner (`sandbox/issue-65-validation.md`).
It found:

- **The race recurs.** A force-cancelled job read 1 s after `completed_at` was `completed` with ten
  steps still `pending` or `in_progress` and no `Complete job` step. It was final 4 s later.
- **A final job has two marks.** In every final read of every case, every step had `status:
  completed` and a conclusion, and the last step was `Complete job`. No unsettled read had either.
  Cancelled and force-cancelled jobs settle like any other.
- **A job cancelled before it got a runner has no steps at all**, and stays so.
- **The job's own `conclusion` cannot tell the two apart.** It was already `cancelled` in the
  unsettled read.

## Decision

**1. Only the "tests did not finish" row waits.** The rule applies only where ADR-013's table would
otherwise give an infrastructure error because the marker is missing or not `success`. A marker that
shows finished tests is proof whatever GitHub still has to write, so a red or green result is never
delayed. A contract error found before that row (a step name listed twice) is not delayed either.

**2. What counts as not final.** A completed job's steps are not final when either holds:
- a listed step has no conclusion, or a `status` other than `completed`;
- the job lists at least one step, but the last is not `Complete job`.

A job with no steps at all is final. That is what a job cancelled before it got a runner looks like,
and it stays an infrastructure error at once.

**3. Nothing is reported while the steps are not final.** The outcome is a new kind, "steps not
final", which is never written to a check run. The Reporter leaves the check run `in_progress` and
exits. The next cycle reads the job again.

**4. The wait is bounded: 5 minutes from the job's `completed_at`.** The limit is a single constant,
not a target setting, because it measures GitHub, not the target's suite. GitHub settled within
seconds in every capture, and 5 minutes stays well inside the 15-minute "reporting pending" alert
(ADR-013 point 6). After 5 minutes, the job is judged as it stands. That gives the same neutral
infrastructure error as before, with one line added: GitHub had still not written down every step
5 minutes after the job completed. It counts toward ADR-017's three neutral results like any other.

A completed job that GitHub gives no `completed_at` cannot be timed, so it is judged at once, as
before this ADR. No capture showed a completed job without one.

**5. The worker does not flag the wait as work.** The worker reads the jobs every cycle through
`mw-observer` anyway. While a job's steps are not final and the wait is not over, the report is not
yet owed, so the worker starts no `watch.yml` run that would find nothing to report. Once the steps
are final or 5 minutes have passed, the report is owed, dated from the job's `completed_at` as
before. So the "reporting pending" alert still counts the wait. The Reporter applies the same rule
in case a cycle reaches it early, and the onboarding dry run waits the steps out like a job still
running.

ADR-013's table, amended:

| Condition | Result |
|---|---|
| The run was deleted after completing (the API returns 404) | `neutral`, alert "outcome unknown" |
| `main-watcher-test` or `main-watcher-tests-finished` found more than once | Contract error: `neutral`, alert "outcome contract broken" |
| `main-watcher-tests-finished` missing, or not `success`, **and the job's steps are not final, less than 5 minutes after it completed** | **Nothing reported yet: the check run stays `in_progress`, and the job is read again** |
| `main-watcher-tests-finished` missing, or not `success` (otherwise) | The tests did not finish: infrastructure error, `neutral`, with an alert listing the step conclusions found, and a note when the steps were still not final |
| The marker rows of ADR-013 | Unchanged |

## Options considered

### Option A — Wait across cycles for final steps, with a bound *(chosen)*

This option adds no new state: `completed_at` and the steps are GitHub's own. It never guesses,
because a job is judged either from a final list or after a wait many times longer than GitHub was
seen to need.

### Option B — Read the job again after a short delay in the same cycle

Wait 10 to 30 s and read once more. It needs no change to the worker. It lost because it still
guesses: a GitHub slower than the delay brings the bug back, only more rarely, while holding a
`watch.yml` run open.

### Option C — Treat a completed job with steps without a conclusion as never final

This option has no bound. It lost because a job that never gains conclusions, such as one whose runner is
lost in a way the sandbox never showed, would hold its check run `in_progress` for ever. Nothing on
the target would be tested meanwhile.

### Option D — Return "not completed" from the table for such a job

This option makes `Outcomes.Read` answer as it does for a running job. It lost because the worker takes "not
completed" to mean no report is owed, and the stale-run rule ignores completed jobs. No deadline
and no alert would ever see the job, so the check run would stay pending for ever.

## Consequences

**Positive**
- A red `main` is locked even when the Reporter reads its job the instant it completes.
- A green run read too early is no longer neutral, so it no longer costs a retest.
- No new state, permission or alert.

**Negative**
- **A slower report in the rare case.** A job read before GitHub has written it down is
  reported a cycle later, usually a minute.
- **A job that never settles is neutral 5 minutes late.** Then it is neutral, with the same alert as before.
- **The rule depends on GitHub's step list.** It relies on the `Complete job` step's name and on
  step `status` values, which GitHub does not document as a contract. A change there would make
  every such job wait 5 minutes and then be judged as it stands. That is slower but still correct,
  and the review trigger covers it.

**Follow-on work**
- None. The scenario suite now names this regression when a red or green unit comes out neutral
  while the job's marker shows success, and keeps the raw jobs responses it reads.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| `Outcomes.Read` and `Outcomes.Final`; the Reporter, `WorkFinder` and the dry run on "steps not final" | TS-U5 (b), TS-U11 over the run-11 shape and the captured fixtures; `sandbox/issue-65-validation.md` | "Reporting pending" alert; the suite's #65 diagnostic |

## Revisit when

- A captured or suite-saved jobs response shows a completed job's steps taking more than a minute to
  become final.
- A final job's steps do not end with `Complete job`.
- GitHub changes how the jobs API lists steps.
