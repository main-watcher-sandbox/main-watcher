---
owner: platform-team
reviewed: 2026-09-22
review_by: 2027-03-15
---

# Issue #26 sandbox validation

Validated on 2026-09-22 against `main-watcher-sandbox/sample-target`, at branch
`claude/issue-26-08226e` (`5a8b5ad`).

**The dry run's logic was exercised against the live GitHub API; its workflow was not.** The
replica push that puts a tree on `main-watcher-sandbox/main-watcher` could not be made from this
session, so `dry-run.yml`, `lock.yml` and TS-S5 at onboarding are **not** covered here. What ran
instead was `MainWatcher.Watcher --dry-run` locally, against the real sandbox target, with a user
token in place of the App token. That exercises every GitHub call the dry run makes — the caller
and gate contents, the workflow dispatch, the run lookup by `run-name`, the jobs read and the
artifact download and schema validation — and the real reusable test workflow, the real CTRF and
the real ADR-013 step contract. It does not exercise the token minting, the
`--validate-target --dry-run` step's wiring into it, or the job summary. See
[What is still owed](#what-is-still-owed).

The trigger worker was scaled to zero throughout and no `watch.yml` run fired, so nothing else
touched the target. The entry used `enabled: false`, which is the state an entry has while it is
being onboarded.

## Runs

| # | Case | Target commit | Target run | Exit | What it covers |
| --- | --- | --- | --- | --- | --- |
| A | Correct setup, green suite | [`a8f74de`](https://github.com/main-watcher-sandbox/sample-target/commit/a8f74de7ba49a49a28fb20da5b0b4397df8e8f1b) | [35788284705](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35788284705) | 0 | All six checks; CTRF validated; no check run, no lock |
| B | `timeout: 25` against a caller that says 30 | — | none dispatched | 1 | Stops at the caller check before dispatching |
| C | Red suite (`failing_tests: ["Alpha"]`) | [`5fd7260`](https://github.com/main-watcher-sandbox/sample-target/commit/5fd7260be62087f00c8d057625dcb5a11a6b4aca) | [35788574110](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35788574110) | 0 | A red suite passes the dry run; the failing test is named; still no check run, no lock |
| D | Resume of run A by ID | — | none dispatched | 0 | Every check again, same CTRF, no second test |

## A — a correct target, green

```
ok     Target entry: `main-watcher-sandbox/sample-target`: `test_command` `dotnet test --no-restore`, `results_glob` `**/TestResults/*.ctrf.json`, `timeout` 30 min, `poll_interval` 1 min, `notify` [pat-actium], `enabled` false.
ok     Test caller: `.github/workflows/main-watcher-tests.yml` calls the reusable workflow, with the entry's three execution settings.
ok     Gate workflow: `.github/workflows/main-watcher-gate.yml` runs `/.github/actions/gate@`. Whether it is a **required** merge-queue check cannot be read from here; TS-S5 confirms that.
ok     Test run: `main-watcher-tests.yml` dispatched for `a8f74de7ba49a49a28fb20da5b0b4397df8e8f1b`: run 35788284705.
ok     Test outcome: Run 35788284705: the tests passed.
ok     CTRF reports: The `main-watcher-ctrf` artifact validates against the CTRF schema: no failing tests; suite time 20287 ms.
Dry run of main-watcher-sandbox/sample-target passed.
```

Ten API requests, and the run was found by its `run-name` — `main-watcher-tests
a8f74de7ba49a49a28fb20da5b0b4397df8e8f1b` — which is the R-14 lookup working against the real
caller rather than a fixture.

**Nothing was written but the dispatch.** The only `main-watcher` check run on that commit is
`106913056917`, started 19:57:35Z with `external_id=35777129312`: a cycle from the scenario suite
nearly two hours before the dry run, pointing at a different target run. The newest issue on the
target is #95 from 19:43:30Z, also older than every run here. GitHub's own
`main-watcher-tests / main-watcher` and `/ report` check runs do appear at 21:43, authored by
`github-actions`; those are the job check runs any workflow run creates, and are not the
watcher's.

## B — a caller that does not match the entry

With `timeout: 25` in the entry against the caller's `timeout-minutes: 30`:

```
FAILED Test caller: main-watcher-sandbox/sample-target: caller test-command, results-glob and timeout-minutes must match targets.yml using literal values.
Dry run of main-watcher-sandbox/sample-target failed at "Test caller".
```

Exit 1 after **one** API request, the read of the caller, and no dispatch: the check that stops a
bad target stops it before a test runs.

## C — a red suite still passes

`sandbox.json` was set to `failing_tests: ["Alpha"]` (`5fd7260`), dry-run, and set back
(`2d58c95`).

```
ok     Test outcome: Run 35788574110: the tests **failed**. The contract holds, so a real cycle would open the `main-broken` lock for this commit: make `main` green before setting `enabled: true`.
ok     CTRF reports: The `main-watcher-ctrf` artifact validates against the CTRF schema: 1 failing test(s): `SampleTarget.Tests.OutcomeTests.Alpha`; suite time 20189 ms.
```

Exit 0. The failing test is the one `sandbox.json` named, read from the artifact the run uploaded,
so the CTRF failure path is confirmed end to end and not only on fixtures.

Two things this run settles beyond the red-suite rule:

- **A red result through the dry run opens nothing.** That commit has **zero** `main-watcher`
  check runs, and no issue was created. A cycle would have locked `main` here; the dry run, having
  created no check run, could not.
- **It judges the `main-watcher` job, not the run.** The dry run reported while run 35788574110 was
  still `in_progress` — its `report` job had not finished. That is ADR-013 point 3 holding in
  practice.

The job's steps were `main-watcher-test=failure`, `main-watcher-tests-finished=success`, which is
the outcome table's red row.

## D — resuming an existing run

`MW_DRY_RUN_ID=35788284705` against the same entry:

```
ok     Test run: Judging run 35788284705 of `main-watcher-sandbox/sample-target`, which was given rather than dispatched. No second test was started.
ok     Test outcome: Run 35788284705: the tests passed.
ok     CTRF reports: The `main-watcher-ctrf` artifact validates against the CTRF schema: no failing tests; suite time 20287 ms.
```

All six checks, and the same suite time as run A to the millisecond, because it is the same
artifact. **Five API requests, every one a GET**: no dispatch, and the target's head was never
read. This is the path that lets a suite slower than the 50-minute token window still have its
CTRF validated (PR #64 review).

## CLI target resolution

The step that scopes the App token, against the same `enabled: false` entry:

| Command | Result |
| --- | --- |
| `--validate-target --dry-run` | Exit 0, `owner=main-watcher-sandbox`, `repo=sample-target` |
| `--validate-target` | Exit 1, "Target disabled; no App token requested." |

So the dry-run flag is exactly what admits an entry that is not yet enabled, and nothing else
does. `--check-targets` parsed the replica's live six-target list and printed each entry.

## Restoration

| What | State |
| --- | --- |
| `sample-target/sandbox.json` | Restored to `failing_tests: []` in `2d58c95`; identical to the file before `5fd7260` |
| `sample-target` locks | None open; no issue was created by any run here |
| Replica `targets.yml` | Untouched — the entry used was a local file, never committed |
| Trigger worker | Left scaled to zero, as it was found |

The target's `main` carries two extra commits from this validation (`5fd7260`, `2d58c95`).
`sandbox/seed-target.sh` resets it, and the scenario suite resets it per unit.

## What is still owed

The replica push was not available in this session, so these remain untested against the sandbox:

1. **`dry-run.yml` itself** — the App token minted with only Contents: read and Actions: write, the
   `--validate-target --dry-run` step feeding `owner`/`repo` into it, the job summary, and the
   70-minute job timeout. The logic underneath is covered above; the wiring is not.
2. **`lock.yml`** — the rename from `sandbox-lock.yml`, the widened guard that accepts a target
   listed in `targets.yml` and refuses anything else, and that `sandbox/scenarios` still dispatches
   it. The guard's shell was checked against a fixture list off GitHub: bare sandbox names, full
   sandbox names and listed targets, quoted and unquoted, allowed; unlisted and arbitrary
   repositories refused.
3. **TS-S5 at onboarding** — the guide's open-lock, queue-the-batch, close-lock procedure, which
   needs (2).

The next scenario suite run exercises (2) as a side effect, since every hand-made lock in it goes
through the renamed workflow.
