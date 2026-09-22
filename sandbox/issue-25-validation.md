---
owner: platform-team
reviewed: 2026-09-22
review_by: 2027-03-15
---

# Issue #25 sandbox validation — the scenario suite

The first runs of `sandbox/run-scenarios.sh`, the one entry point for TS-S1 to TS-S18, for
[MainWatcher#25](https://github.com/Actium-Group-Corporation/MainWatcher/issues/25). They ran on 2026-09-21 against the
`main-watcher-sandbox` org, on ten pool targets, `sample-target` and `sample-target-2` to `-10`, watched by the private
replica `main-watcher-sandbox/main-watcher`. Each run's report, logs and TS-S8 table are under
`sandbox/scenarios/out/`, which is not committed.

All times UTC.

## Summary

| Run | Commit | Units | Result | Time |
|---|---|---|---|---|
| 1 | `fd9b693` | TS-S13 only (`--only TS-S13 --no-status`) | Passed | 15 min, plus 7 min preparing |
| 2 | `fd9b693` | All 25 (`--no-status`) | 19 passed, 6 failed, every failure in the suite's own code | 100 min, plus 1 min preparing |
| 3 | `0917946` | All 25, posting the `scenario-suite` status | Stopped after TS-S15 found a fault in Main Watcher | 37 min |
| 4 | `5b8d6a5` | All 25, 10 targets | Stopped once the `main-watcher` App's API budget ran out | 80 min |
| 5 | `d0245e8` | TS-S6 only, to deploy the new error messages | Passed; showed the 403 was the installation's rate limit | 17 min |
| 6 | `04dee2c` | All 25, 6 targets | Stopped after TS-S14 found a race in Main Watcher | 72 min |
| 7 | `664b6a7` | All 25, 6 targets | Stopped once TS-S7 (a) and TS-S11 failed on the API budget | 40 min |
| 8 | `664b6a7` | All 25, 6 targets | Stopped once TS-S17 (a) failed on the API budget | 65 min |
| 9 | `22ffc39` | All 25, 6 targets | 23 passed, 2 failed, both in the suite's own code | 116 min |
| 10 | `1e68709` | All 25, 6 targets | **Passed**, and posted `scenario-suite` = `success` | 115 min |

**Run 10 passed in full on 2026-09-22, and `1e68709` on `main` carries `scenario-suite` = `success`.** Runs 3 to 8 were
stopped by the API budget: the suite needed more requests than the sandbox's `main-watcher` App installation allows, 5000
an hour shared by every target's cycles, and with six targets the busy part of a run used them at about 10,000 an hour.
[MainWatcher#60](https://github.com/Actium-Group-Corporation/MainWatcher/issues/60) cut a cycle from about 210 requests to
about 20 (`issue-60-validation.md`). Runs 9 and 10 are recorded there; run 9's two failures were both in the suite's own
code, and run 10 passed with both fixed. What the earlier runs found is below.

**Duration.** The issue asked for about 30 minutes. That cannot be reached. TS-S16 (h)'s unstoppable run has to wait for
its run deadline, 32 minutes after its job starts. Then come 15 minutes to the force-cancel and 15 more to the alert.
With the worker's one-minute cycles and the release of the run, that unit alone took 93 minutes in run 2. It starts
first, and the other units fit around it. Run 2, on ten targets, took 100 minutes. On six targets, a full run should
take about 2 h 15 min.

## Run 1 — the machinery

TS-S13 alone, to test preparation and one unit end to end. Preparation took 7 minutes:
- it published the gate and its five workflow variants;
- it took the tree to the replica;
- it built and rolled out the worker image;
- it seeded the ten targets;
- it waited for each to be green.

The seeding pushed a new head to every target. The worker started a cycle for each within two minutes, and every one
came back green. TS-S13 then passed in 9.9 minutes. Both check runs' timing sections matched the CTRF artifact exactly
(suite time 20.2 s and 20.3 s, the five slowest tests, the retry flag). The second measured its change against the
first. Both `report` jobs succeeded and saved the reporter's history artifact.

## Run 2 — the first full run

18:20:52 to 20:01:38. Preparation took 1 minute, since the sandbox already ran the commit. The outage units prepared
their locks while the worker ran. The outage lasted from 18:30:22 to 19:07:47, and the pool units that need the worker
waited for it.

| Result | Scenarios | Minutes |
|---|---|---|
| Passed | TS-S1, TS-S2, TS-S3, TS-S4 and TS-S5, TS-S6, TS-S9, TS-S10, TS-S12, TS-S13, TS-S14 (a), (b), (d), TS-S14 (c), TS-S16 (a), (b), (c), TS-S16 (d), TS-S16 (e), TS-S16 (f), TS-S16 (g), TS-S16 (h) run deadline, TS-S17 (a), TS-S18 | 1.8 to 58.4 |
| Failed | TS-S7 (a) and TS-S11, TS-S7 (b), TS-S15, TS-S17 (b) | 46.0 each |
| Failed | TS-S16 (h) unstoppable | 93.3 |
| Failed | TS-S8 | 1.5 |

TS-S16 (g) passed on its first run. It was the first use of the `ts-s16-held-job` variant: a test job held in a
concurrency group by the target's `sandbox-hold` workflow, then let go after 9 minutes.

The six failures had three causes, all in the suite. None was a fault in Main Watcher.

**The restoring sweep was never queued.** At 18:47:38 the suite enabled `watch.yml` and dispatched the sweep that ends
the outage, one second apart. GitHub accepted the dispatch and returned run
[35640689240](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35640689240). That run then stayed
`queued` with no jobs, while other runs in the replica started normally. A cancel answered "Cannot cancel a workflow
run that is completed". A force-cancel answered 409, "Cannot cancel a workflow run that has not been queued yet". After
20 minutes the suite gave up, and all four outage units failed at their last step. Up to then, each had passed its
outage clauses: the merge with no lock, the lapsed lease, the gate failing open. The fix:
- `EnableWatch` now waits until the workflow reads `active`, and 10 seconds more;
- `Replica.Started` dispatches again, up to 3 times, if a run has no jobs 3 minutes after its dispatch;
- the outage's own one-target dispatches use the same check, and disable `watch.yml` again only once the run is queued.

**TS-S8 probed an issue that did not exist.** The credential-scope script probes issue writes with an empty update
to the target's newest issue. `sample-target-6` was seeded for the suite and had no issue at all, so both Apps' probes
went to `issues/` and got 404 instead of 403. The script now opens and closes a probe issue, as the operator, on a
target that has none. It then passed every check. The unit now also saves the script's whole table when the script
fails, since the log kept only its end.

**The unstoppable-run unit left a 2-minute `targets.yml` entry behind.** Every clause of TS-S16 (h)'s unstoppable half
passed:
- the cancel came at the run deadline and was refused;
- the force-cancel came 15 minutes later and was refused too;
- 15 minutes after that, alert #41 said the run could not be stopped;
- the check run stayed `in_progress`, and the newer head was not tested;
- once the run was deleted, the check run ended neutral, "Outcome unknown".

The newer head was then not tested. Its caller named the default 30-minute timeout, but the entry still said 2
minutes, so each cycle refused the caller ("caller test-command, results-glob and timeout-minutes must match
targets.yml"). That is the watcher doing its job. The unit now restores the default entry when it releases the target.

## Run 3 — a fault in Main Watcher

20:02:28 to 20:44, on the fixed commit. Preparation took 6 minutes. This time the outage ended as planned: the restoring
sweep, [35651812538](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35651812538), was queued, ran and
succeeded in under 3 minutes. The worker was running again at 20:36:02. Then TS-S17 (b), TS-S7 (b), and TS-S7 (a) with
TS-S11 all passed, in 28 to 30 minutes each.

TS-S15 failed on its last clause. These parts passed:
- lock sample-target-3#5 stayed closed and was marked `reconciled=complete`;
- the lock listed both merges made during the lapse, #6 and #7, with a comment for each and the override comment;
- alert #51 reported #6.

But #7 was never added to alert #51. The recovering cycle reported the two merges 2 seconds apart, and opened a second
alert, #52, with the same title "Merged while locked on main-watcher-sandbox/sample-target-3". `Alerts.Raise` finds the
open alert with the same title through the issue list, and GitHub's list did not yet hold #51, created 2 seconds earlier.
It is the same gap TS-S17 found for lock issues in #21. The effect was a duplicate alert, where ADR-012 promises one
alert per condition; a replay could repeat an alert the same way.

The fix, in `5b8d6a5`: `Alerts` remembers each alert it opened for 2 minutes and uses it when the list does not show it
yet. The window is short, so an alert a person closes afterwards does not take comments. A unit test fakes the lagging
list. Run 3 was stopped at 20:44, since it could no longer post a pass, and run 4 started on the fixed commit.

Two things came up during run 3, outside the suite:
- **Actions minutes.** The sandbox org is on the Free plan, where only private repos use its 2000 Actions minutes a
  month. The replica had used 1501 of them, about 700 in run 2 alone: about 500 in `watch.yml` cycles and about 200 in
  its own `ci.yml`, which each `targets.yml` edit triggers. At that rate run 3 would have run out part-way, so the
  replica was made public at about 20:13, like every other sandbox repo.
- **The replica's CI.** Its `lint` job failed on this branch's new `sandbox-hold.yml`: shellcheck SC2015, for an
  `A && B || C` check. The check is now an `if` statement, and actionlint 1.7.12 passes on every workflow.

## Run 4 — the API budget

20:43 to 22:03, on ten targets. All four outage units passed, TS-S15 among them, so the alert fix worked: both merges
went into one alert. The restoring sweep ran as it should. Then from 21:54 every `watch.yml` cycle, on every target,
failed with only "403 (Forbidden)". TS-S16 (a) to (c) and TS-S16 (g) failed waiting for cycles, and the run was
stopped.

A bare 403 cannot tell a missing permission from a rate limit, so the gateway now names the refused request. It also
gives GitHub's message and the rate-limit headers (`d0245e8`). Run 5 deployed that. The next refused cycle said "API
rate limit exceeded for installation ID 162224105", with `x-ratelimit-remaining: 0`, refilling at 22:15:29. That is the
`main-watcher` App's installation on the sandbox org: one budget of 5000 requests an hour for every target's cycles. From
the refill time, the hour began at 21:15:29, so run 4 used the whole budget in 39 minutes. That is about 7,700 an hour,
from about 160 cycles. Run 2's busiest hour had had about as many cycles and stayed just under.

Two things came of it:
- Each cycle now ends by logging what the installation has left ("API budget of this installation: … requests left").
- The suite uses six targets by default, always including `sample-target-10` (`04dee2c`).

Nothing raised an alert during the 20 minutes every cycle failed. R-13 watches only the worker's own Apps. That gap is
[MainWatcher#60](https://github.com/Actium-Group-Corporation/MainWatcher/issues/60).

## Run 6 — a race in Main Watcher, and a cost that grows

22:25 to 23:37, six targets. All four outage units passed, along with the TS-S16 (h) run deadline, TS-S17 (a), TS-S18,
TS-S10 and TS-S16 (f). The budget line showed the busy phase using about 1,500 requests in 10 minutes, a pace of about
8,900 an hour. That is more than run 4, on fewer targets.

TS-S14 (a), (b), (d) failed waiting for the override comment on lock sample-target#68, which the unit had closed by hand
at 23:22:14. The comment never came, because nothing asked for another cycle:
- The cycle that started at 23:20:45 noted overrides before the close, and reconciled after it.
- Reconciliation found the lock closed and marked it `reconciled=complete`.
- The worker asks for a cycle only for a closed lock that is not complete, so it saw no work.

On an idle target the comment would have waited for the hourly sweep. In runs 2 and 4 an unrelated cycle happened to
come in time.

The fix, in `664b6a7`: reconciliation marks complete only the closed locks the same cycle's `NoteOverrides` saw closed.
A lock closed in between stays incomplete, so the worker asks for one more cycle, and that cycle notes the override first.

Looking for the cost found the other half of `664b6a7`. `NoteOverrides` read the comments of every closed lock from the
last 30 days, and asked who closed it, on every cycle. `sample-target` had 29 closed locks by then, so each of its cycles
spent about 58 requests on that alone, and the cost grows as locks build up, in production too. With the race fixed,
`reconciled=complete` means the closure has been judged, so `NoteOverrides` now skips complete locks.

## Runs 7 and 8 — still over the budget

Run 7 started at 23:37 on `664b6a7`, and its first cycles cost about 27 requests each, down from about 50. But its
restoring sweep ran at 00:13. Two of its legs drew on a budget that runs 6 and 7 had already emptied (0 left, refilling
at 00:15:32), while the other four legs reported about 2,800 left. TS-S7 (a) and TS-S11 failed because the sweep's leg
for their target was refused. TS-S15 and TS-S17 (b) passed. The run was stopped. Which cycles share which budget is not
visible from outside: the refused requests all named installation 162224105.

Run 8 started at 00:16 with the budgets full. All four outage units passed, and so did the TS-S16 (h) run deadline. At
00:53, 37 minutes into the budget hour, 4,120 to 4,230 requests were left. The pool units then started, and 56 cycles in
23 minutes used the rest, over 70 requests each. The budget was empty from shortly before its refill at 01:16:34. TS-S17
(a) failed: its lock opened too late for the gate still running, because the report cycle was refused. The run was stopped.

Each cycle now also logs how many requests it sent, by endpoint ("API requests this cycle: …"), so the next run shows
where the cost is. That is the first step of #60.

## Where it stands

Every unit has passed at least once:

| Unit | Runs it passed in |
|---|---|
| TS-S1, TS-S2, TS-S3, TS-S9, TS-S12, TS-S16 (a), (b), (c), TS-S16 (e), TS-S16 (g) | 2 |
| TS-S13 | 1, 2 |
| TS-S4 and TS-S5, TS-S14 (c), TS-S16 (d) | 2, 4 |
| TS-S6 | 2, 3, 4, 5 |
| TS-S7 (a) and TS-S11 | 3, 4, 6, 8 |
| TS-S7 (b) | 3, 4, 6, 8 |
| TS-S10, TS-S16 (f), TS-S18 | 2, 4, 6 |
| TS-S14 (a), (b), (d) | 2, 4 |
| TS-S15 | 4, 6, 7, 8 |
| TS-S16 (h) run deadline | 2, 4, 6, 8 |
| TS-S17 (a) | 2, 4, 6 |
| TS-S17 (b) | 3, 4, 6, 7, 8 |
| TS-S8 | Its script passed every check when run on its own, after the fix; not yet in a suite run |
| TS-S16 (h) unstoppable | Every clause but the last passed in run 2; the fixed unit has not yet run to the end |

In total, the runs found:
- two faults in Main Watcher, the duplicate alert and the override race;
- one growing API cost, the override check;
- four faults in the suite itself: the stuck dispatch, TS-S8's probe, the unstoppable unit's `targets.yml` entry, and
  `sandbox-hold.yml`'s lint.

A release needs a full passing run, and that waits on #60.
