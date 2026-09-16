# Manual watcher

Issue #9 supplies one Planner/Reporter cycle in `.github/workflows/watch.yml`.
It creates and completes `main-watcher` check runs; lock issues, alerts, stale-run
cancellation and the automatic trigger are separate backlog items.

Configure the `reporter` environment with `MAIN_WATCHER_APP_ID` (variable) and
`MAIN_WATCHER_PRIVATE_KEY` (secret). Install that App on each target with
Contents read, Actions write and Checks write. The workflow obtains a token
scoped to the selected repository after building the watcher.

## Configuration

`targets.yml` is a mapping with a `targets` array. Unknown fields and duplicate
keys or repositories are rejected. Each entry supports:

| Field | Meaning | Default |
| --- | --- | --- |
| `repo` | Required `owner/repo` | None |
| `test_command` | Command configured in the target caller | `dotnet test --no-restore` |
| `results_glob` | Reports uploaded by the target caller | `**/TestResults/*.ctrf.json` |
| `timeout` | Test deadline, integer minutes from 1 to 340 | 30 |
| `poll_interval` | Minimum interval, positive integer minutes | 15 |
| `notify` | Owner/team mentions, list of strings; used by future lock reporting | `[]` |
| `enabled` | Whether the target participates | `true` |

The command, glob and timeout must match `with` in the target's copied
`templates/main-watcher-tests.yml`. That caller owns test execution and secrets;
the watcher only dispatches its `sha` and `check_run_id` inputs. Its literal
`run-name: main-watcher-tests ${{ inputs.sha }}` exposes the tested SHA for the
fallback lookup; the workflow run's `head_sha` is not the tested SHA.

## Run a cycle

```sh
gh workflow run watch.yml -f target=owner/repo
```

The first cycle creates a check and starts the target workflow. Dispatch again
after the target's `main-watcher` job completes to report it. Until the worker
is implemented, reporting is manual. All cycles share a concurrency group and
do not cancel a running cycle. Repeated dispatches do not retest a successful
or failed head. `force=true` bypasses only the three-neutral-result cap, never
an active check or the poll interval.

The reusable caller's job and literal step names determine the outcome. The
Reporter ignores unrelated jobs, reads all jobs pages from the latest attempt,
and validates and merges all JSON report files in `main-watcher-ctrf` (except
`timings.json`). It reads ZIP entries without extracting or executing files.
Missing, malformed, oversized or undownloadable reports yield “failing tests
unknown” but cannot turn a failed test step green or neutral.

An ambiguous or invisible dispatch remains pending and blocks another start.
The next cycle retries linking it, without dispatching again. Jobs API failures
also leave checks pending. Automated cancellation and infrastructure alerts are
tracked separately in #13 and #18. Check discovery currently walks main's commit
history to find App-owned checks, favoring correctness for this manual first path;
large-history targets will need a more economical index before production rollout.

## Validation

Run `dotnet test` and the CI-pinned actionlint. The core suite includes real xUnit
reports and a GitHub reusable-caller jobs response, with provenance in its fixtures
directory. Sandbox execution uses the private watcher replica described in
`sandbox/README.md`. The [issue #9 validation record](../sandbox/issue-9-validation.md)
links the passing and failing checks, their Planner and Reporter cycles, and
the target restoration evidence.
