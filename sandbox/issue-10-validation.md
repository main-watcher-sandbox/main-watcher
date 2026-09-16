---
owner: platform-team
reviewed: 2026-09-16
review_by: 2027-03-15
---

# Issue #10 sandbox validation

Validated on 2026-09-16 with watcher implementation `95c558f`, deployed to the
private `main-watcher-sandbox/main-watcher` replica, against
`main-watcher-sandbox/sample-target`. The target has no `notify` list and no CODEOWNERS
file.

## Red opens a real lock

| Step | Evidence |
| --- | --- |
| Failing head: `failing_tests: ["Alpha"]` | `8e835fc90f741e019f5c8d54231e316ac6fcfb9e` |
| Planner | [35146398137](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35146398137) |
| Target tests | [35146585656](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35146585656), `main-watcher` job `failure` |
| Reporter | [35146724292](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35146724292) |
| Lock | [sample-target#11](https://github.com/main-watcher-sandbox/sample-target/issues/11), created 20:28:58Z |
| Check | [failure](https://github.com/main-watcher-sandbox/sample-target/runs/104963656306), completed 20:28:59Z |
| Alert | [main-watcher#1](https://github.com/main-watcher-sandbox/main-watcher/issues/1), "Lock issues on main-watcher-sandbox/sample-target mention nobody" |

The lock was authored by `main-watcher[bot]` (resolved from the token's `app-slug`),
labelled `main-broken`, mentioned nobody, and showed the failing commit, the failing
Alpha test, the target run and the marker
`first_red=8e835fc… lease_until=2026-09-17T00:28:57Z reported_check=104963656306 reported_sha=8e835fc…`.
The check output linked the lock. The alert was opened by `github-actions[bot]` with the
watcher repo's workflow token.

**Dispatch note.** The Planner's dispatch returned no run and no run appeared, while the
check stayed `in_progress` awaiting recovery, as designed. A 4xx would have completed it
as neutral, so the call failed with a 5xx or a network error; GitHub status showed no
incident. The identical request, replayed by hand a minute later, returned HTTP 200 with
`workflow_run_id` 35146585656. The Reporter cycle's recovery linked that run by its
`main-watcher-tests <sha>` title and reported it. The gateway does not log the swallowed
dispatch error, so the status code is unknown.

## TS-S4 with a real lock

| PR | Label | Result |
| --- | --- | --- |
| [#12](https://github.com/main-watcher-sandbox/sample-target/pull/12) README change | none | Queued 20:30:02Z; gate [35146938518](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35146938518) failed: "`main` is locked by #11 … Not labelled: #12"; removed 20:30:51Z |
| [#13](https://github.com/main-watcher-sandbox/sample-target/pull/13) restores `sandbox.json` | `fixes-main` | Gate [35147048688](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35147048688) passed; merged 20:31:55Z as `fd25d494efd5b88a53da57f02a88e6e1bf01a740` |

The PRs were queued one at a time, so the queue did not batch them (that case is TS-S5).

## Green closes the lock (TS-S3, first part)

| Step | Evidence |
| --- | --- |
| Planner | [35147125872](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35147125872) |
| Target tests | [35147197367](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35147197367), `success` |
| Reporter | [35147315111](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35147315111) |
| Check | [success](https://github.com/main-watcher-sandbox/sample-target/runs/104966161964), completed 20:34:39Z |

Lock #11 got a `main-watcher[bot]` comment naming green commit `fd25d49` and the target
run, and was closed by `main-watcher[bot]` at 20:34:39Z with `state_reason: completed`.
GitHub's timestamps have one-second resolution, so the write order is shown by the
Reporter's log and by the unit tests, not by these times.

## Restoration

`sandbox.json` on `main` has blob `248577d26226bbb013bf1803d279b50f0d0366b1`, the
original. PR #12 was closed and its branch deleted, alert #1 was closed with a note, and
no `main-broken` issue or PR is open on the target.
