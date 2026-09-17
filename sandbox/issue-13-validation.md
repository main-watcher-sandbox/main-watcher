---
owner: platform-team
reviewed: 2026-09-17
review_by: 2027-03-15
---

# Issue #13 sandbox validation

Validated on 2026-09-17 against `main-watcher-sandbox/sample-target`, with watcher
implementation `551ab9c` deployed to the private `main-watcher-sandbox/main-watcher`
replica. Cycles were dispatched by hand, standing in for the trigger worker, and "by hand"
means the `pat-actium` account. Alerts are issues in the replica. The replica's
`targets.yml` has `poll_interval: 1`. The workflow variants are branches of
`main-watcher-sandbox/gate` (see the [sandbox README](README.md#workflow-variants)). All
target runs below are `main-watcher-tests` runs of `sample-target`; all cycles are `watch`
runs of the replica.

Before the scenarios, alert main-watcher#2 ("mention nobody", from #11) was the only open
alert, and no lock was open.

## A stray run: cancelled after the tests finished

A helper script wrote an empty `sandbox.json` (`591ecb8`), so every test failed. The run was
cancelled by hand as soon as that was noticed, but the cancel landed after the test step had
finished. It is recorded because it is TS-S16 (d)'s "cancelled by hand after the test step
finished" case.

| Step | Evidence |
| --- | --- |
| Planner | [35236393234](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35236393234): check 105253565760, target run [35236476827](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35236476827) |
| Target run | Run `cancelled`; job `main-watcher` `cancelled`, with `main-watcher-test` `failure` and `main-watcher-tests-finished` `success`; `report` job `cancelled` |
| Reporter | [35236565128](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35236565128): check `failure`; [sample-target#24](https://github.com/main-watcher-sandbox/sample-target/issues/24) opened, listing the failing tests |
| Defaults restored | `a699bbd`: Planner [35236685445](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35236685445), target run [35236769402](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35236769402) `success`, Reporter [35236879989](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35236879989) closed #24 as green |

## TS-S16 (b): restore fails, twice in a row

`fail_restore: true`, commit `aae0bfe`.

| Step | Evidence |
| --- | --- |
| Planner | [35237012099](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35237012099): check 105255653736, target run [35237088775](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35237088775) |
| Target run | `Restore` `failure`; `main-watcher-test` and `main-watcher-tests-finished` `skipped`; the `report` job still `queued` when reported |
| Reporter | [35237176953](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35237176953): check `neutral`, output "Infrastructure error: the tests did not finish." with all 15 step conclusions; alert [main-watcher#3](https://github.com/main-watcher-sandbox/main-watcher/issues/3) "Infrastructure error on main-watcher-sandbox/sample-target" created 14:59:30Z; no lock |
| Retest of the same head, a minute later | Planner [35237421176](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35237421176): check 105257122302, target run [35237516575](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35237516575), restore failed again |
| Reporter | [35237594483](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35237594483): check `neutral`; a comment on #3 at 15:03:11Z; alert [main-watcher#4](https://github.com/main-watcher-sandbox/main-watcher/issues/4) "Infrastructure errors twice in a row on main-watcher-sandbox/sample-target" created 15:03:13Z, naming the previous check run 105255653736; no lock |

## TS-S16 (c): the test step renamed

The caller used `@ts-s16-renamed-step` (`a2904cb`), with `failing_tests: ["Alpha"]`
(`708d3aa`).

| Step | Evidence |
| --- | --- |
| Planner | [35237724547](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35237724547): check 105258096865, target run [35237803294](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35237803294) |
| Target run | `main-watcher-tests-renamed` `failure`, `main-watcher-tests-finished` `success` |
| Reporter | [35237910858](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35237910858): check `neutral`, output "Outcome contract broken: `main-watcher-tests-finished` succeeded without a `main-watcher-test` result." with the steps; alert [main-watcher#5](https://github.com/main-watcher-sandbox/main-watcher/issues/5) "Outcome contract broken on main-watcher-sandbox/sample-target"; no lock although Alpha failed |

Every other run in this record is a reusable-workflow caller whose `main-watcher-test` step
the Reporter found in the real jobs response, under the job name
`main-watcher-tests / main-watcher`.

## TS-S16 (a): the upload fails

The caller back on `@main` (`aed08b0`), with `failing_tests: ["Alpha"]` and
`fail_upload: true` (`56ed57f`).

| Step | Evidence |
| --- | --- |
| Planner | [35238019075](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35238019075): check 105259116074, target run [35238097551](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35238097551) |
| Target run | `main-watcher-test` `failure`, marker `success`, `sandbox upload switches` `failure`; the run has no artifacts |
| Reporter | [35238210527](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35238210527): check `failure`; lock [sample-target#25](https://github.com/main-watcher-sandbox/sample-target/issues/25) opened 15:09:00Z, its failing tests "failing tests unknown" |

## TS-S16 (d): late failures and cancels

With lock #25 open, each red result below added a comment to it.

| Case | Commit and switches | Target run | Reporter | Result |
| --- | --- | --- | --- | --- |
| Upload hangs past its timeout | `18415ee`: `failing_tests: ["Beta"]`, `hang_upload: true` | [35238450386](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35238450386): marker `success`, `sandbox upload switches` `failure` after its 5-min timeout | [35239137029](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35239137029) | Check 105260346844 `failure`; comment on #25 at 15:17:05Z |
| Cancelled by hand during the hung upload, at 15:19:32Z | `a9683b9`: `failing_tests: ["Gamma"]`, `hang_upload: true` | [35239348803](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35239348803): run `cancelled`; marker `success`, `sandbox upload switches` `cancelled` | [35240029695](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35240029695) | Check 105263432211 `failure`; comment on #25 at 15:25:14Z |
| Cancelled by hand during the test step, at 15:28:11Z | `999e4d1`: `hang_test: true` | [35240229079](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35240229079): `main-watcher-test` `cancelled`, marker `skipped` | [35240555776](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35240555776) | Check 105266453988 `neutral`; comment on alert #3 at 15:30:03Z; no comment on #25; no "twice in a row", because the previous check was red |

The stray run above covers a cancel after the test step finished with a successful upload.

## TS-S16 (e): a test hangs past a 2-min target timeout

The replica's `targets.yml` was set to `timeout: 2` (`eb506a0`) and the caller to
`timeout-minutes: 2` (`d44d8ac`), with `hang_test: true` still set.

| Case | Target run | Reporter | Result |
| --- | --- | --- | --- |
| No step `timeout-minutes`: the wrapper's deadline | [35240907417](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35240907417) (Planner [35240827342](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35240827342)): `main-watcher-test` ran 15:32:52Z–15:34:52Z and ended `failure`; marker `skipped` | [35241196593](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35241196593) | Check 105268774288 `neutral`; comments on alerts #3 and #4 (the previous check was neutral); no lock comment |
| `timeout-minutes: 1` on the test step: caller on `@ts-s16-step-timeout` (`997785e`) | [35241512092](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35241512092) (Planner [35241425654](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35241425654)): "The action 'main-watcher-test' has timed out after 1 minutes.", step `failure`; marker `skipped` | [35241699303](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35241699303) | Check 105270825806 `neutral`; comments on alerts #3 and #4; no lock comment |

In both cases GitHub reported the test step as `failure`, and only the skipped marker kept the
result from being red.

## TS-S16 (f): the report job never gets a runner

`targets.yml` back to `timeout: 30` (`6a55be7`), the caller on `@ts-s16-report-stuck` with
`timeout-minutes: 30` (`4a5670b`), and `failing_tests: ["Alpha"]` (`7e40494`).

| Step | Evidence |
| --- | --- |
| Planner | [35241998184](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35241998184): check 105272777955 created 15:43:24Z, target run [35242083057](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35242083057) |
| Target run at 15:44:23Z | Job `main-watcher` `completed` `failure` with marker `success`; job `report` `queued`; run `queued` |
| Reporter | [35242189073](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35242189073): check `failure` at 15:45:15Z; comment on #25 at 15:45:14Z |

Stale-run detection and cancellation are not built yet (#18), so nothing could mark this run
stale.

## Replay of a neutral report

Not exercised in the sandbox: no fault switch stops the Reporter between a neutral result's
alert and its check completion. `ReplayedNeutralReportDoesNotRepeatItsAlert` in
`MainWatcher.Core.Tests` covers it.

## Restoration

| Step | Evidence |
| --- | --- |
| (f) past the queue deadline | At 16:13:30Z, 30 min after check 105272777955 was created, the `report` job was still `queued`. Cycle [35245252383](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35245252383): "No eligible head"; the check stayed `failure`, completed at 15:45:15Z, and #25 still had one comment for it |
| Stuck run | Target run 35242083057 cancelled by hand |
| Caller back on `@main` with `timeout-minutes: 30`, then `sandbox.json` restored | `9aa8ad5`, `7f89da9` |
| Green run | Planner [35245395616](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35245395616): check 105284485276, target run [35245494387](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35245494387) `success` |
| Green closes #25 | Reporter [35245662743](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35245662743): #25 closed by `main-watcher[bot]`, with a comment ending `<!-- main-watcher check=105284485276 sha=7f89da915f22bbe25089e90253260dbb75f21a9c closed=green -->`; check `success` |
| Idle cycle | [35245874315](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35245874315): "No eligible head" |

`sandbox.json` on `main` has blob `248577d26226bbb013bf1803d279b50f0d0366b1`, the original,
and `main-watcher-tests.yml` has blob `9c73ed70e798b58b4c13d2279433b9a589c75cc6`, as before
the scenarios. No `main-broken` issue or PR is open on the target. Alerts main-watcher#3 to #5
were closed with a comment pointing here; #2 stays open until `notify` is configured.

The replica's `targets.yml` went to `timeout: 2` and back in two commits of its own
(`eb506a0`, `6a55be7`) on top of `551ab9c`, so its content matches MainWatcher's again, but
its `main` is no longer an ancestor of later MainWatcher commits. The PR #43 re-check below
deployed with a merge commit on top of it instead of a force push. The three workflow variant branches stay in the gate repo
for re-runs.

## PR #43 review re-check

The review found that any neutral check run started the "twice in a row" streak, and that a
failed neutral alert still let the check run complete. `89b1ea0` records the kind in the check
run's output title, counts only a previous "Infrastructure error", and makes the neutral alerts
required writes. It was deployed to the replica as `d834e6a`, a merge of `89b1ea0` onto the
replica's `main` with `89b1ea0`'s tree.

| Step | Evidence |
| --- | --- |
| Contract error: caller on `@ts-s16-renamed-step` (`f3692fc`), Alpha failing (`7f76634`) | Planner [35250678471](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35250678471), target run [35250758697](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35250758697), Reporter [35250867744](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35250867744): check 105302210433 `neutral`, output title "Outcome contract broken"; alert [main-watcher#6](https://github.com/main-watcher-sandbox/main-watcher/issues/6) |
| Restore fails next: caller on `@main` (`c307f3e`), `fail_restore: true` (`12725c7`) | Planner [35251115276](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35251115276), target run [35251187850](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35251187850), Reporter [35251245736](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35251245736): check 105303634120 `neutral`, title "Infrastructure error"; alert [main-watcher#7](https://github.com/main-watcher-sandbox/main-watcher/issues/7); no "twice in a row", because the previous check was a contract error |
| Retest of the same head | Planner [35251468249](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35251468249), target run [35251547891](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35251547891), Reporter [35251625197](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35251625197): check 105304837737 `neutral`, title "Infrastructure error"; a comment on #7 and alert [main-watcher#8](https://github.com/main-watcher-sandbox/main-watcher/issues/8) "Infrastructure errors twice in a row", read from the previous check run's title through the check-runs API |
| Restored: `sandbox.json` (`327ee4e`) | Planner [35251860075](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35251860075), target run [35251942609](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35251942609) `success`, Reporter [35252036143](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35252036143): check `success`, title "Tests passed" |

`sandbox.json` and `main-watcher-tests.yml` again have blobs `248577d` and `9c73ed7`, no lock
is open, and alerts #6 to #8 were closed with a comment pointing here. A failed neutral alert
leaving the check `in_progress` was not forced in the sandbox; unit tests cover it.
