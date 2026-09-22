---
owner: platform-team
reviewed: 2026-09-22
review_by: 2027-03-15
---

# Issue #26 sandbox validation

Validated on 2026-09-22 against `main-watcher-sandbox/sample-target`, with `bd461ce` on the
`main-watcher-sandbox/main-watcher` replica (`4f63d89`, tree identical to the branch).

Everything #26 adds is covered: the dry run through its own workflow, its resume path, the
renamed `lock.yml` and both branches of its guard, and TS-S5 as the onboarding guide tells a team
to run it. Nothing here ran a cycle: the trigger worker was scaled to zero throughout, no
`watch.yml` run fired, and the entry under test was `enabled: false` — the state an entry has
while it is being onboarded.

## The dry run

| # | Case | How it ran | Target run | Result |
| --- | --- | --- | --- | --- |
| A | Correct target, green suite | CLI, user token | [35788284705](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35788284705) | Six checks, CTRF validated, 10 requests |
| B | `timeout: 25` against a caller saying 30 | CLI | none | Exit 1 at the caller check, after 1 request |
| C | Red suite (`failing_tests: ["Alpha"]`) | CLI | [35788574110](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35788574110) | Exit 0; the failing test named from the artifact |
| D | Resume of A by ID | CLI | none | Six checks, 5 requests, all GET |
| E | Correct target, green suite | **`dry-run.yml`** on the replica | [35790248819](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35790248819) | Six checks, CTRF validated, 85 s |
| F | Resume of E by ID | **`dry-run.yml`**, `run_id` input | none | Six checks, 5 requests, all GET |

A to D ran the CLI directly with a user token, before the replica carried the branch; E and F
are the same code through [`dry-run.yml`](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35790219448)
with the App token the workflow mints. Both paths agree.

### E — the workflow, end to end

```
ok     Target entry: `main-watcher-sandbox/sample-target`: … `timeout` 30 min, `poll_interval` 1 min, `notify` [pat-actium], `enabled` false.
ok     Test caller: `.github/workflows/main-watcher-tests.yml` calls the reusable workflow, with the entry's three execution settings.
ok     Gate workflow: `.github/workflows/main-watcher-gate.yml` runs `/.github/actions/gate@`. …
ok     Test run: `main-watcher-tests.yml` dispatched for `2d58c950…`: run 35790248819.
ok     Test outcome: Run 35790248819: the tests passed.
ok     CTRF reports: The `main-watcher-ctrf` artifact validates against the CTRF schema: no failing tests; suite time 20240 ms.
Dry run of main-watcher-sandbox/sample-target passed.
```

Ten API requests, 85 seconds from dispatch to verdict. The run was found by its `run-name`,
`main-watcher-tests 2d58c950…`, which is the R-14 lookup against a real caller rather than a
fixture.

**It wrote nothing but the dispatch.** The tested commit has **zero** `main-watcher` check runs
and the target gained no issue. GitHub's own `main-watcher-tests / main-watcher` and `/ report`
check runs do appear, authored by `github-actions`: those are the job check runs any workflow run
creates, not the watcher's.

### C — a red suite passes, and still locks nothing

`sandbox.json` was set to `failing_tests: ["Alpha"]` (`5fd7260`), dry-run, and set back
(`2d58c95`).

```
ok     Test outcome: Run 35788574110: the tests **failed**. The contract holds, so a real cycle would open the `main-broken` lock for this commit: make `main` green before setting `enabled: true`.
ok     CTRF reports: The `main-watcher-ctrf` artifact validates against the CTRF schema: 1 failing test(s): `SampleTarget.Tests.OutcomeTests.Alpha`; suite time 20189 ms.
```

Exit 0, and the failing test is the one `sandbox.json` named, read out of the artifact the run
uploaded. Two things this settles beyond the red-suite rule:

- **A red result through the dry run opens nothing.** That commit carries zero `main-watcher`
  check runs and no issue was created. A cycle would have locked `main` here; the dry run, having
  created no check run, could not.
- **It judges the `main-watcher` job, not the run.** The verdict came while run 35788574110 was
  still `in_progress`, its `report` job unfinished. That is ADR-013 point 3 in practice.

The job's steps were `main-watcher-test=failure`, `main-watcher-tests-finished=success`: the
outcome table's red row.

### F — resuming, through the workflow

```
ok     Test run: Judging run 35790248819 of `main-watcher-sandbox/sample-target`, which was given rather than dispatched. No second test was started.
ok     Test outcome: Run 35790248819: the tests passed.
ok     CTRF reports: The `main-watcher-ctrf` artifact validates against the CTRF schema: no failing tests; suite time 20240 ms.
```

Five requests, every one a GET, and the same suite time as E to the millisecond, because it is the
same artifact. The target's count of `main-watcher-tests.yml` runs was **183 before and 183
after**, so no second test was started. The run is named `dry run main-watcher-sandbox/sample-target
(run 35790248819)`. This is the path that lets a suite slower than the 50-minute token window still
have its CTRF validated (PR #64 review).

### CLI target resolution

The step that scopes the App token, against the same `enabled: false` entry:

| Command | Result |
| --- | --- |
| `--validate-target --dry-run` | Exit 0, `owner=main-watcher-sandbox`, `repo=sample-target` |
| `--validate-target` | Exit 1, "Target disabled; no App token requested." |

So the dry-run flag is exactly what admits an entry that is not yet enabled, and nothing else
does. `--check-targets` parsed the replica's live six-target pool and printed each entry.

## `lock.yml`

Renamed from `sandbox-lock.yml` in #26; the old file is gone from the replica and the new one
works. Its guard was exercised on both sides:

| Dispatch | Guard step | Token step | Why |
| --- | --- | --- | --- |
| `target=acme/checkout`, not listed | **failure** | **skipped** | Neither a sandbox repo nor in `targets.yml` |
| `target=acme/checkout`, listed in `targets.yml` | **success** | failure | The guard admitted it; the App is not installed on a repository that does not exist |
| `target=sample-target` (bare) | success | success | The sandbox branch of the guard |

```
##[error]acme/checkout is neither a main-watcher-sandbox target nor listed in targets.yml, so it may not be locked by hand.
```

The middle row is the point: `acme/checkout` does not exist, and was listed in the replica's
`targets.yml` only to show that the guard admits a repository because the list names it, not
because of its owner. It was removed straight afterwards. That the token step is **skipped** on a
refusal matters too: no App token is minted at all for a repository that may not be locked.

## TS-S5 at onboarding

Run exactly as [onboarding.md](../docs/onboarding.md) step 7 describes, with `sample-target` still
`enabled: false` and the worker down.

1. `lock.yml … -f action=open -f lease_hours=1` opened
   [#97](https://github.com/main-watcher-sandbox/sample-target/issues/97), titled "main is broken
   (hand-made lock)", authored by `app/main-watcher`, carrying
   `<!-- main-watcher lease_until=2026-09-22T23:09:26Z -->` — App-authored with a live lease, which
   is what the gate enforces.
2. Two pull requests on the same base: #98 labelled `fixes-main`, #99 unlabelled. Both passed
   their required checks, `main-watcher-gate` and `sandbox-slow-check`; the gate always passes on
   `pull_request`.
3. Enqueued back to back. #99's queue branch was
   `gh-readonly-queue/main/pr-99-8a624c2b…`, whose base is the head of #98's group rather than
   `main`: the second group was built on the first and so held both pull requests, which is the
   batch TS-S5 asks for (merge limit 2).
4. **#98 merged; #99 was removed from the queue.** The two gate runs:

   ```
   run 35791004401  pr-98 group  success
   ##[notice]`main` is locked by #97 (…/issues/97), but every PR in this merge group is labelled `fixes-main`: #98.

   run 35791011516  pr-99 group  failure
   ##[error]`main` is locked by #97 (…/issues/97). Only PRs labelled `fixes-main` can merge. Not labelled: #99 TS-S5 plain (MainWatcher#26 validation).
   ```

5. `lock.yml … -f action=close` closed #97 as `main-watcher[bot]`, `state_reason=completed`, with
   the comment "Closed by the lock workflow." Because the App closed it, **no human override was
   recorded**, which is what the guide's teardown relies on.

These are the two assertions the suite's own TS-S5 makes — that the later group was built on the
earlier one, and that the gate failed it naming the unlabelled pull request — reached here through
the hand-made lock the guide uses rather than through a red `main`.

## Restoration

| What | State |
| --- | --- |
| `sample-target/sandbox.json` | Restored to `failing_tests: []` (`2d58c95`); identical to the file before `5fd7260` |
| Lock #97 | Closed by the App; no open `main-broken` issue remains |
| Pull requests | #98 merged (TS-S5 requires it to), #99 closed; both `validation/*` branches deleted |
| Merge queue | Empty; no `gh-readonly-queue` branches left |
| Replica `targets.yml` | Restored byte for byte to the scenario suite's six-target pool |
| Trigger worker | Left scaled to zero, as it was found |

`sample-target`'s `main` advanced to `8a624c2b`, the merge of #98, and carries three commits from
this validation. `sandbox/seed-target.sh` resets the target, and the scenario suite resets it per
unit.

## Not covered

That `sandbox/scenarios` still dispatches the renamed workflow is checked by inspection only: its
`Replica.cs` names `lock.yml`, and the workflow answered that filename here, but no scenario unit
was run. The next full suite run exercises it, since every hand-made lock in it goes through that
dispatch.
