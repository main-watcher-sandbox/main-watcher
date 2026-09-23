---
owner: platform-team
reviewed: 2026-09-23
review_by: 2027-03-15
---

# Issue #65 sandbox evidence

Captured on 2026-09-23 against `main-watcher-sandbox/sample-target-7`, with
`sandbox/run-scenarios.sh capture-jobs` (`sandbox/scenarios/JobsCapture.cs`). Each case committed its
switches to a side branch and ran the target's `main-watcher-tests.yml` by hand from it, so no
check run, lock or `watch.yml` cycle was involved. The jobs API was read with `filter=latest`, as the
gateway reads it: every 10 s while the job ran, every second from when its tests ended or it was
stopped until a minute after it completed, then every 15 s until five minutes after. Every distinct
response was kept.

This is the evidence #65's decisions 2 and 3 asked for, before any rule is written. It changes no
behaviour: PR 1 only reads step `status`, step `completed_at` and the job's `conclusion`.

## Cases

| Case | Target run | How it ended | Distinct reads after `completed_at` | First read after completion |
| --- | --- | --- | --- | --- |
| completed-1 | [35891997126](https://github.com/main-watcher-sandbox/sample-target-7/actions/runs/35891997126) | `Alpha` fails | 7 at 0 s to +15 s | final |
| completed-2 | [35892690810](https://github.com/main-watcher-sandbox/sample-target-7/actions/runs/35892690810) | `Alpha` fails | 8 at +1 s to +15 s | final |
| completed-3 | [35893379724](https://github.com/main-watcher-sandbox/sample-target-7/actions/runs/35893379724) | `Alpha` fails | 7 at 0 s to +11 s | final |
| cancelled | [35894067224](https://github.com/main-watcher-sandbox/sample-target-7/actions/runs/35894067224) | cancelled 20 s into a hanging test | 1 at +1 s | final |
| force-cancelled | [35894853098](https://github.com/main-watcher-sandbox/sample-target-7/actions/runs/35894853098) | force-cancelled 20 s into a hanging test, accepted at once | +1 s, +5 s | **not final** |
| no-runner | [35895539448](https://github.com/main-watcher-sandbox/sample-target-7/actions/runs/35895539448) | `runs-on` a label no runner has; cancelled after 30 s queued | 1 at +1 s | no steps |

"Final" means every step has `status: completed` and a conclusion, and the last step is
`Complete job`. After the first final read, the `main-watcher` job did not change again in the five
minutes that followed, in any case.

## The race, reproduced

Run 11's failure (#65) did not recur in the three ordinary runs: each first read after completion was
already final. It recurred with the force-cancel. Read 1 s after `completed_at`, the job was already
`completed`, `cancelled`, with its steps as they stood while it ran:

| Read | Job | Steps | Last step | Without a conclusion |
| --- | --- | --- | --- | --- |
| +1 s | `completed`, `cancelled` | 15 | `Post Build the test runner` | 10: `main-watcher-test` (`in_progress`), `main-watcher-tests-finished` (`pending`) and every later step (`pending`) |
| +5 s | `completed`, `cancelled` | 16 | `Complete job` | none: `main-watcher-test` `cancelled`, `main-watcher-tests-finished` `skipped` |

Here the result is the same either way, neutral, since the tests did not finish. In run 11 it was
not, because the tests had finished and failed. The shape is the same: a completed job whose step
list GitHub has not yet finished writing. It settled within 4 s.

## What the evidence says about the provisional rule

- **Signal 1, a listed step without a conclusion.** It held in every read. Every final read of every
  case had all steps concluded, and the one unsettled read had ten without. That includes the
  cancelled and force-cancelled jobs, which is the case the issue feared would never gain conclusions.
- **Signal 2, a list that does not end with `Complete job`.** It held in every read too. While the job
  runs, and in the unsettled read, the list already starts with `Set up job` but ends with the last
  post step; `Complete job` appears only once the list is final. Run 11's list (6 steps, starting at
  `Build the test runner`) did not end with it either.
- **No steps at all.** A job cancelled before it got a runner completed `cancelled` with an empty
  step list, and stayed so. As decided, that must not count as "not final": it is neutral at once.
- **The job's own `conclusion`.** It was already `cancelled` in the unsettled read, so it cannot tell
  a settled job from an unsettled one. It is kept for the record only.
- **The 5-minute limit.** GitHub settled within 4 s here, and run 11's job was final when it was
  read again later, so 5 minutes leaves a wide margin.

Not captured: a lost runner, which the sandbox cannot cause on demand. By the prose records in
`sandbox/issue-18-validation.md`, its step list was final. If one ever never gains conclusions, the
5-minute limit still makes it neutral.

## Fixtures

These responses are kept in `tests/MainWatcher.Core.Tests/Fixtures/` and read through the gateway by
`GatewayTests`:

| Fixture | Read |
| --- | --- |
| `jobs-failed-final.json` | completed-1, 0 s after completion |
| `jobs-cancelled-final.json` | cancelled, +1 s |
| `jobs-force-cancelled-unsettled.json` | force-cancelled, +1 s |
| `jobs-force-cancelled-final.json` | force-cancelled, +5 s |
| `jobs-no-runner.json` | no-runner, +1 s |

## The scenario suite

The suite now keeps the raw jobs response of every target test run it reads, under
`jobs/<target>/` in its output folder: the first response with the `main-watcher` job completed, and
the latest (`sandbox/README.md`). It was built but not run for this record; the capture above is the
evidence.
