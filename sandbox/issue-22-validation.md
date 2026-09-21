---
owner: platform-team
reviewed: 2026-09-21
review_by: 2027-03-15
---

# Issue #22 sandbox validation

Validated on 2026-09-21 against `main-watcher-sandbox/sample-target`, with the watcher
implementation deployed to the private `main-watcher-sandbox/main-watcher` replica. The
trigger worker was not running (the sandbox cluster was down), so each cycle was a hand
dispatch of `watch.yml` for the target: one to start the test, one to report it.

TS-S13's check-run half asks that the check run show suite time, the 5 slowest tests and the
retry flag. Issue #22 adds the change from the last green run, and requires the durations to
come from the watcher's own conversion of CTRF milliseconds rather than from the reporter action
(R-17). Both runs used `timed_tests: true`, which runs the 50 ms, 2 s and 20 s tests.

## Runs

| Run | Watcher | Target commit | Target run | Check run | What it covers |
| --- | --- | --- | --- | --- | --- |
| 1 | `1154005` | [`a31746d`](https://github.com/main-watcher-sandbox/sample-target/commit/a31746dde9f6fcaaf758079fa80e0e2503b7ddcb) | [35606646936](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35606646936) | 106355221920, green | Suite time, the slowest 5, a last green run that recorded no time |
| 2 | `fb0bcf9` | [`756b4d6`](https://github.com/main-watcher-sandbox/sample-target/commit/756b4d60cfbf9717aee4a713cd986567540c2521), `flaky_test: true` | [35607241602](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35607241602) | 106357217159, green | The change from run 1, the retry flag |

`flaky_test` was set back to `false` afterwards (`fe09582`).

## Run 1

The check run's timing section:

```
- Suite time: 20.5 s wall clock; 22.4 s summed across tests, which run in parallel
- Change from the last green run: unknown, because the last green run, efee9e7, recorded no suite time
- Retried: no

| SampleTarget.Timing.Tests.Duration20Seconds.Takes20Seconds | 20.0 s |
| SampleTarget.Timing.Tests.Duration2Seconds.Takes2Seconds | 2.0 s |
| SampleTarget.Timing.Tests.Duration50Milliseconds.Takes50Milliseconds | 150 ms |
| SampleTarget.Tests.OutcomeTests.Beta | 76 ms |
| SampleTarget.Timing.Tests.Baseline.Runs | 24 ms |

<!-- main-watcher suite_ms=20547 -->
```

Against the run's `main-watcher-ctrf` artifact: the two CTRF summaries span 20547 ms, the tests
add up to 22435 ms, and the five largest `duration` values are 20091, 2094, 150, 76 and 24 ms,
in that order. `timings.json` agrees: `wallClockMs` 20547, `summedMs` 22435, `retried` false.
The last green run before this one, `efee9e7`, was reported before this section existed, which
is the "recorded no suite time" case.

**One fault found.** The first build truncated to the tenth, so 20091 ms read `20.0 s` and 2094 ms
`2.0 s`, where the job summary shows 20.1 s and 2.1 s for the same values. `fb0bcf9` rounds, as
the job summary does, and shows a time that rounds to a whole minute in minutes rather than as
`60.0 s`. Run 2 is on that build.

## Run 2

```
- Suite time: 20.2 s wall clock; 22.4 s summed across tests, which run in parallel
- Change from the last green run: -343 ms (-1.7%) against 20.5 s at a31746d
- Retried: yes, 1 failed test was run a second time

| SampleTarget.Timing.Tests.Duration20Seconds.Takes20Seconds | 20.1 s |
| SampleTarget.Timing.Tests.Duration2Seconds.Takes2Seconds | 2.1 s |
| SampleTarget.Timing.Tests.Duration50Milliseconds.Takes50Milliseconds | 147 ms |
| SampleTarget.Tests.OutcomeTests.Beta | 55 ms |
| SampleTarget.Tests.BehaviourTests.Flaky (retried) | 38 ms |

<!-- main-watcher suite_ms=20204 -->
```

Against the artifact: wall clock 20204 ms, summed 22438 ms, largest durations 20089, 2089, 147,
55 and 38 ms, and `Flaky` is the one test with `retries: 1`; `timings.json` has `retried` true.
The change is run 2's 20204 ms less run 1's 20547 ms, read from run 1's `suite_ms` marker, and
the percentage is of run 1's time.

## Result

TS-S13's check-run half passes. With the job-summary half passed on 2026-09-16 (MainWatcher#8),
TS-S13 has passed as a whole. Every duration shown is xUnit v3's CTRF millisecond value, converted
by the watcher, so R-17 cannot reach the check run whatever the reporter action does.

The red path was not run in the sandbox, since it would open a lock for no new evidence: the
section is written before the lock details on a red result as on a green one, which the unit
tests cover.
