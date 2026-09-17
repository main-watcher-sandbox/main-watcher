---
owner: platform-team
reviewed: 2026-09-17
review_by: 2027-03-15
---

# Issue #12 sandbox validation

Validated on 2026-09-17 against `main-watcher-sandbox/sample-target`, with watcher
implementation `a888d50` deployed to the private `main-watcher-sandbox/main-watcher`
replica. A finding from that run was fixed in `f006467`, which was then deployed and
re-checked. Cycles were dispatched by hand, standing in for the trigger worker. The fault
switch was the replica's `MW_SANDBOX_EXIT_AFTER` variable, and "by hand" means the
`pat-actium` account. The last green commit before the scenarios was `65887a6`.

## TS-S14 (a): stopped after creating the lock

`MW_SANDBOX_EXIT_AFTER=create`.

| Step | Evidence |
| --- | --- |
| Failing head: `failing_tests: ["Alpha"]` | `9f924fb3a22f2f602d19248017e05aa9362bdcc4` |
| Planner | [35227532563](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35227532563): check 105223064193, target run [35227611251](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35227611251), `main-watcher` job `failure` |
| Reporter, stopped | [35227754378](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35227754378): "exiting after the Reporter's create write"; [sample-target#15](https://github.com/main-watcher-sandbox/sample-target/issues/15) created 13:33:42Z; check still `in_progress` |
| Replay, switch still set | [35227871107](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35227871107): "Reported check 105223064193", check `failure` at 13:34:36Z |

After the replay, #15 was the only new issue and had no comments. Its marker named
`reported_check=105223064193`. The replay skipped the create, so it did not stop again.

## TS-S14 (b): stopped after the comment, then after the update

`MW_SANDBOX_EXIT_AFTER=comment,update`, with lock #15 open.

| Step | Evidence | #15 afterwards |
| --- | --- | --- |
| Failing head: `failing_tests: ["Beta"]` | `079e82e5eae2cf1198d91b2eeb227f545c877706` | |
| Planner | [35227985301](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35227985301): check 105224562509, target run [35228054462](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35228054462), `failure` | |
| Reporter, stopped | [35228192719](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35228192719): "exiting after the Reporter's comment write" | 1 comment; marker still `reported_check=105223064193`; check `in_progress` |
| Replay, stopped | [35228281509](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35228281509): "exiting after the Reporter's update write" | 1 comment; marker `reported_check=105224562509`; check `in_progress` |
| Replay | [35228395777](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35228395777): "Reported check 105224562509" | 1 comment; check `failure` at 13:39:43Z |

The single comment named `079e82e`, the failing Beta test and the target run, and ended in
`<!-- main-watcher check=105224562509 -->`.

## TS-S3, override part

| Step | Evidence |
| --- | --- |
| #15 closed by hand | `closed_by: pat-actium`, 13:40:08Z |
| Next cycle | [35228531404](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35228531404): "No eligible head", "Posted 1 override comment(s) on locks closed by hand" |
| Override comment | 13:40:50Z, by `main-watcher[bot]`: "`pat-actium` closed this lock by hand. That is an override: the merge queue accepts every pull request again, but `main` is still red at `079e82e`…", marked `closed=override` |
| The gate lifts | Unlabelled [PR #16](https://github.com/main-watcher-sandbox/sample-target/pull/16) (README line) queued 13:41:39Z; gate [35228722242](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35228722242) `success`; merged 13:42:30Z as `7d62facd63de710d68f37d5dd79123e00e89f6ce` |
| A failure on a newer commit opens a new issue | `7d62fac` still failed Beta and opened [sample-target#17](https://github.com/main-watcher-sandbox/sample-target/issues/17), whose body says "The previous lock, …/issues/15, was closed by hand." (see (d)) |

Every later cycle left #15 with its two comments, so the override comment was posted once.

## TS-S14 (d): stopped after creating the lock, which a human then closes

`MW_SANDBOX_EXIT_AFTER=create`.

| Step | Evidence |
| --- | --- |
| Planner for `7d62fac` | [35228811143](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35228811143): check 105227431294, target run [35228885732](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35228885732), `failure` |
| Reporter, stopped | [35229009968](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35229009968): "exiting after the Reporter's create write"; #17 created; check `in_progress` |
| #17 closed by hand | `closed_by: pat-actium`, 13:45:44Z |
| Replay | [35229141825](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35229141825): "Reported check 105227431294", "Posted 1 override comment(s) on locks closed by hand" |

No new lock was created. The check completed as `failure` at 13:46:28Z, with the output
"This result was already written to lock issue …/issues/17, which has been closed since,
so no lock was opened." #17 got the override comment at 13:46:33Z, naming `pat-actium`
and `7d62fac`.

## Finding: an interrupted create skipped its alert

The target has no `notify` list and no CODEOWNERS, so each lock should raise the
"mention nobody" alert. In (a) and (d), the Reporter stopped after the create and before
that alert, and the replay skipped the create and the alert with it. `f006467` makes the
replay raise the alert when the open lock was opened by this check (`first_red` is the
commit under test).

Re-checked with `f006467` and `MW_SANDBOX_EXIT_AFTER=create`:

| Step | Evidence |
| --- | --- |
| Failing head: `failing_tests: ["Alpha"]` | `5f5b45c69400894ee3f20670c450c1efd80751be` |
| Planner | [35229961229](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35229961229): check 105231371679, target run [35230028703](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35230028703), `failure` |
| Reporter, stopped | [35230168740](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35230168740): "exiting after the Reporter's create write"; [sample-target#18](https://github.com/main-watcher-sandbox/sample-target/issues/18) created; no alert |
| Replay | [35230257909](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35230257909): check `failure`; alert [main-watcher#2](https://github.com/main-watcher-sandbox/main-watcher/issues/2) got a comment at 13:56:46Z naming #18 |

## Restoration

| Step | Evidence |
| --- | --- |
| First restore of `sandbox.json` | `ea13fd5`: Planner [35229271904](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35229271904), target run [35229356170](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35229356170) `success`, Reporter [35229502720](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35229502720) `success` with no lock open |
| Restore after the re-check | `8da6d752e377ea96d24d50286cebe1ce63efc7d8`: Planner [35230377646](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35230377646), target run [35230509125](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35230509125) `success` |
| Green closes #18 | Reporter [35230655588](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35230655588): #18 closed by `main-watcher[bot]`, with a comment ending `<!-- main-watcher check=105233051082 closed=green -->`; check `success` |
| Idle cycle | [35230775259](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35230775259): "No eligible head", and no override comment on #18 |

`sandbox.json` on `main` has blob `248577d26226bbb013bf1803d279b50f0d0366b1`, the
original. The `MW_SANDBOX_EXIT_AFTER` variable and the PR #16 branch were deleted, and no
`main-broken` issue or PR is open on the target. Alert main-watcher#2 was already open
from #11 and stays open until `notify` is configured.

## Not exercised in the sandbox

Closing a newer duplicate lock, a replay after a comment on a lock closed since, and the
override of a retest of the same commit (which needs a neutral retest, ADR-017) are covered
by unit tests in `MainWatcher.Core.Tests` (TS-U8). TS-S14 (c) needs the "reporting pending"
alert (#15).
