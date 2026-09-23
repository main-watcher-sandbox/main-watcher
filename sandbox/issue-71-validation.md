---
owner: platform-team
reviewed: 2026-09-23
review_by: 2027-03-15
---

# Issue #71 sandbox evidence

Run on 2026-09-23 against `MainWatcher@10f7117`, with every sandbox target reseeded from
`sandbox/sample-target` on the `xunit.v3` package at 4.0.0 (ADR-021).

## What xUnit 4.0.0 changed

The template was first run locally on 3.2.2 and on 4.0.0, with `failing_tests: ["Beta"]`:

| | 3.2.2 | 4.0.0 |
| --- | --- | --- |
| Microsoft Testing Platform | 1.x (`xunit.v3.mtp-v1`) | 2.3.3 (`xunit.v3.mtp-v2`) |
| With `--report-ctrf` | Two reports | Exit 5, "Zero tests ran", no report |
| CTRF option | `--report-ctrf` | `--report-xunit-ctrf` |
| Where reports go | `tests/<P>/bin/Debug/net10.0/TestResults/` | `TestResults/` at the repo root |
| A test's `suite` | GUID string | `[assembly hash, collection hash, "Ns.Class", "Method"]` |
| `CtrfReader.Read` on `main` | Known, Beta listed | **Unknown**: `suite` fails the schema |

`--filter-method` and `--ignore-exit-code 8` behave as before. A retry overwrote the reports in
place and left an empty but valid report for the project with nothing to retry.

## A red, flaky run on 4.0.0

[sample-target-9 run 35909100260](https://github.com/main-watcher-sandbox/sample-target-9/actions/runs/35909100260),
with `failing_tests: ["Beta"]` and `flaky_test` on, dispatched by hand, so no check run or lock was
involved:

- `main-watcher-test` failed and `main-watcher-tests-finished` succeeded, so the tests are judged.
- The test runner retried Beta and Flaky. The uploaded reports show Beta failed with `retries: 1`,
  and Flaky passed with `retries: 1, flaky: true`. `timings.json` has `retried: true`, a wall clock
  of 20294 ms and a summed time of 22514 ms.
- The pinned `ctrf-io/github-test-reporter` read the array `suite` without complaint. Its
  summary shows 1 failed and 1 flaky, and its slowest tests at 20.1s and 2.1s, against CTRF's
  20103 ms and 2101 ms.

Its artifact is the source of the 4.0.0 fixtures in `tests/MainWatcher.Core.Tests/Fixtures` and
`tests/MainWatcher.TestRunner.Tests/Fixtures/Xunit4ParallelWithRetry`.

## The dry run

The CLI with a user token, `enabled: false`, against `sample-target-9`, which is outside the suite's
pool:

| Case | Code | Result |
| --- | --- | --- |
| Judge run 35909100260 (red) by `MW_DRY_RUN_ID` | `main` (`8082c25`) | **Failed** at "CTRF reports": missing, empty or not valid CTRF |
| The same | `10f7117` | Passed: "1 failing test(s): `SampleTarget.Tests.OutcomeTests.Beta`; suite time 20294 ms", 5 requests |
| Fresh dispatch, green template | `10f7117` | Passed: [run 35910329791](https://github.com/main-watcher-sandbox/sample-target-9/actions/runs/35910329791), no failing tests, suite time 20284 ms, 11 requests, 95 s |

## The scenario suite

`sandbox/run-scenarios.sh`, the full suite on six 4.0.0 targets: **26 of 26 units passed in
114 minutes**, plus 10 minutes preparing, with 3330 API calls. `scenario-suite` and
`scenario-suite/ts-s13` are `success` on `10f7117`.

- **TS-S13** passed on `sample-target-4`. Each check run's suite time matches the artifact's wall
  clock (20323 ms and 20326 ms), and its slowest tests match CTRF. The retry flag matches
  `timings.json`: no on the first run, yes on the retried one. The job summary lists the timed tests
  in scale with CTRF's milliseconds, and ranks them in order.
- **Locks list the test class as the suite.** Lock #54 on `sample-target-2`, opened by TS-S7 (b),
  lists `SampleTarget.Tests.OutcomeTests.Alpha (SampleTarget.Tests.OutcomeTests)`. Before this
  change, the suite would have shown a GUID on 3.x and been unreadable on 4.x.
