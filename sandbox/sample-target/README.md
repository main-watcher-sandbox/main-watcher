# Sandbox target

A synthetic .NET/xUnit v3 repo that Main Watcher's scenario tests (TS-001) run against.
Everything it does is steered by committing a change to [`sandbox.json`](sandbox.json).

This repo is seeded from `sandbox/sample-target` in the MainWatcher repo by
`sandbox/seed-target.sh`. Change the template there, not here; scenario commits here are
throwaway.

## Running the tests

```
dotnet test
```

It uses Microsoft Testing Platform (`global.json`), so the xUnit v3 retry filter works:
`dotnet test -- --filter-method <name>`. Each test project writes
`TestResults/<project>.ctrf.json` (ADR-007). The exit code is non-zero when a test fails.

Two test projects exist so a run produces two CTRF reports to merge:

| Project | Tests |
|---|---|
| `SampleTarget.Tests` | `OutcomeTests.Alpha`, `.Beta`, `.Gamma`; `BehaviourTests.SlowSuite`, `.Hang`, `.Flaky` |
| `SampleTarget.Timing.Tests` | `Duration50Milliseconds`, `Duration2Seconds`, `Duration20Seconds` (run in parallel); `Baseline.Runs` |

Tests whose switch is off are skipped, not passed. `Baseline.Runs` always runs, because
Microsoft Testing Platform exits with code 8 when a project runs no tests.

## Switches

| Switch | Default | Effect | Used by |
|---|---|---|---|
| `failing_tests` | `[]` | Names of `OutcomeTests` to fail (`"Alpha"`, `"Beta"`, `"Gamma"`), or `"*"` for all three | Most scenarios |
| `timed_tests` | `true` | Runs the 50 ms, 2 s and 20 s tests | TS-S13 |
| `slow_suite_minutes` | `0` | When above 0, `SlowSuite` waits that many minutes | TS-S2 (slow variant) |
| `hang_test` | `false` | `Hang` sleeps forever and ignores cancellation | TS-S16 (e) |
| `flaky_test` | `false` | `Flaky` fails on the first attempt of a workflow run and passes on the retry | TS-S13 retry flag |
| `fail_restore` | `false` | `Directory.Build.props` points restore at an unreachable feed, with an empty packages folder: `dotnet restore` fails with NU1301 | TS-S16, TS-S18 |
| `fail_upload` | `false` | **Stub.** Makes the CTRF upload step fail. Read by the sandbox build of the reusable test workflow (MainWatcher#7), not by this repo | TS-S16 |
| `hang_upload` | `false` | **Stub.** Makes the CTRF upload step hang past its timeout. Read by the sandbox build of the reusable test workflow (MainWatcher#7), not by this repo | TS-S16 (d), (h) |

An unknown key makes every test except `Baseline.Runs` fail, so a typo in `sandbox.json` shows up at once.

`Flaky` remembers its first attempt in a temp file keyed by `GITHUB_RUN_ID` and
`GITHUB_RUN_ATTEMPT`. Run locally, it fails once per machine, then passes.

## Variants

- `main-watcher-sandbox/sample-target`: the defaults above.
- `main-watcher-sandbox/sample-target-slow`: `slow_suite_minutes: 5`, `timed_tests: false`.

## Merge queue

`main` has the ruleset `main merge queue` (`sandbox/rulesets/main-merge-queue.json`):
merge limit 2, minimum group 1, 1-minute wait, so batched groups can be formed (TS-S5).
Organisation admins bypass it, so scenario scripts can push to `main` directly.

`.github/workflows/main-watcher-gate.yml` is added by `seed-target.sh` from MainWatcher's
`templates/`, using the gate action from `main-watcher-sandbox/main-watcher`. Its
`main-watcher-gate` job is a required check. While an App-authored `main-broken` issue with a
valid lease is open, merge groups fail unless every PR in them is labelled `fixes-main`.

## Self-test

`sandbox-selftest.yml` (manual) restores, runs the tests and checks both CTRF reports.
It is not the Main Watcher test workflow; `main-watcher-tests.yml` is added in MainWatcher#7.
