---
owner: platform-team
reviewed: 2026-09-18
review_by: 2027-03-15
---

# Issue #20 sandbox validation — reconciliation through a lock's closure

TS-S9 and TS-S15 for [MainWatcher#20](https://github.com/Actium-Group-Corporation/MainWatcher/issues/20)
(ADR-008, ADR-015), run on 2026-09-18 against `main-watcher-sandbox/sample-target`, watched by the
private replica `main-watcher-sandbox/main-watcher`. The trigger worker was scaled to zero from
16:08 UTC to 16:38, so every cycle below was dispatched by hand and nothing ran between a merge and
the human close that follows it. The public `main-watcher-sandbox/gate` repo was not republished:
this issue changes nothing that runs inside a target.

All times UTC.

## Backfill: sixteen closed locks in one pass

The replica took the tree under test at 16:09 and one cycle was dispatched
([run 35366837841](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35366837841)).
Every App lock the sandbox has ever opened was closed and none carried `reconciled=complete`, so
the first pass reconciled all sixteen of them from one repository-activity read:

| Lock | Merges in its window | Reported |
|---|---|---|
| #3 | 3 | 0 |
| #11 | 1 | 0 |
| #29 | 1 | 1 — [sample-target#28](https://github.com/main-watcher-sandbox/sample-target/pull/28) |
| #34 | 1 | 1 — [sample-target#35](https://github.com/main-watcher-sandbox/sample-target/pull/35) |
| #10, #14, #15, #17, #18, #24, #25, #26, #27, #30, #31, #32 | 0 | 0 |

The negative cases are the ones that matter, because a rule that reported everything would also
have passed this test. Lock #3's three merges were `Merge pull request #2/#5/#6`, and each of those
pull requests carried `fixes-main` before it merged — #2 labelled 16:40:49 and merged 16:46:58 on
2026-09-16, and so on — read from `/issues/{n}/events` with the App's Issues permission and nothing
more. **This is the first confirmation that a pull request's label history is readable without a
Pull requests permission**, which is what ADR-008 promised and what the whole design of this pass
rests on.

Lock #34's report is **TS-S7's outstanding clause** from
[issue #19](issue-19-validation.md): the unlabelled `sample-target#35` that merged during that
lock's lease lapse. It is now on the closed issue, in a comment, and in the alert.

## TS-S9 — the gate fails open on an API error, and the next run reports the merge

The target's gate workflow was given an invalid token on `main` at 16:13:27, so every gate API call
answers 401.

| Time | What happened |
|---|---|
| 16:13:47 | Lock [sample-target#37](https://github.com/main-watcher-sandbox/sample-target/issues/37) opened by `sandbox-lock.yml`, `lease_hours=4`, so the lease is valid and the gate would enforce it |
| 16:14:52 | [sample-target#38](https://github.com/main-watcher-sandbox/sample-target/pull/38), unlabelled, added to the merge queue |
| 16:15:29 | [Gate run 35367354905](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35367354905): `##[warning]The gate could not read the lock state or the group's pull requests from the GitHub API: GET repos/main-watcher-sandbox/sample-target/issues?state=open&labels=main-broken&per_page=100: HTTP 401. The merge group passes.` The job summary is headed **LOCK STATUS UNKNOWN — failed open**, and the `main-watcher/gate-fail-open` job ran |
| 16:15:42 | #38 merged, unlabelled, onto a locked `main` |
| 16:25:07 | The next watcher cycle reported it (below) |

## TS-S15 — a human closes the lock before the watcher recovers

`sample-target#39` was queued next. It merged unlabelled and was given `fixes-main` twelve seconds
later, which is clause (b): the label a pull request carries now is not the one it merged with.

| Time | What happened |
|---|---|
| 16:23:01 | [sample-target#39](https://github.com/main-watcher-sandbox/sample-target/pull/39) merged, unlabelled |
| 16:23:13 | `fixes-main` added to #39 — after the merge |
| 16:23:22 | Lock #37 closed **by hand** by `pat-actium`, before any watcher cycle had looked at either merge |
| 16:23:31 | The gate's token restored |
| 16:23:38 | Cycle dispatched: [run 35368205379](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35368205379) |
| 16:25:01 | `Posted 1 override comment(s) on locks closed by hand.` (ADR-004) |
| 16:25:07 | `Lock #37: reconciled 2 merge(s) up to 2026-09-18T16:23:22Z, reporting 2; its window is complete.` |

Lock #37's body afterwards:

```
**Merged while locked**

- [#38](…/pull/38) merged into `main` at 2026-09-18T16:15:42Z without the `fixes-main` label. <!-- main-watcher merged_while_locked pr=38 -->
- [#39](…/pull/39) merged into `main` at 2026-09-18T16:23:01Z without the `fixes-main` label. <!-- main-watcher merged_while_locked pr=39 -->

<!-- main-watcher lease_until=… last_reconciled=2026-09-18T16:23:00Z reconciled=complete -->
```

and one comment per merge, so the closed issue's participants are notified, plus the ADR-004
override comment. In the replica, alert
[main-watcher#22](https://github.com/main-watcher-sandbox/main-watcher/issues/22), "Merged while
locked on main-watcher-sandbox/sample-target", carries #38 in its body and #39 as a comment — one
issue, not two (ADR-012). The lock was never reopened.

Clause (b) is the second row: #39 carried `fixes-main` at the moment the pass ran and was still
reported, because the label went on after it merged.

## What the sandbox changed: the merge time is `merged_at`, not the commit date

That first pass reported #38 at **16:14:51** and #39 at **16:22:11** — the merge commits' committer
dates. Their real `merged_at` values are 16:15:42 and 16:23:01. The merge queue **builds an entry's
commit when the entry starts running its checks and merges it when they pass**, so the two differ by
however long the queue took: fifty seconds here, and longer on a busy queue. ADR-015 point 8 judges
the labels at `merged_at`, and judging at the earlier time would report a pull request whose
`fixes-main` went on while it waited in the queue.

The fix costs nothing: a pull request's `merged` event is in the same `/issues/{n}/events` timeline
as its `labeled` and `unlabeled` events, so the moment is read by the request already being made,
with no Pull requests permission. The commit's date now stands in only when the timeline holds no
merge.

Re-run afterwards with the fix in place, against a lock whose **lease had expired** — the other way
the gate fails open (ADR-014), and the same recovery shape:

| Time | What happened |
|---|---|
| 16:29:26 | Lock [sample-target#40](https://github.com/main-watcher-sandbox/sample-target/issues/40) opened with `lease_hours=-1`, so `lease_until` is an hour in the past |
| 16:30:59 | [Gate run 35368894591](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35368894591): `##[warning]The lock is open but its lease is missing, unreadable, expired or more than 24 h ahead: #40 (lease_until=2026-09-18T15:29:24Z).` — **LOCK LEASE EXPIRED — failed open** |
| 16:30:20 | The merge commit `f19aa71` was written |
| 16:31:10 | [sample-target#41](https://github.com/main-watcher-sandbox/sample-target/pull/41) merged, unlabelled |
| 16:31:5x | Lock #40 closed by hand |
| 16:33:03 | `Lock #40: reconciled 1 merge(s) up to 2026-09-18T16:31:21Z, reporting 1; its window is complete.` |

The row reads `merged into `main` at 2026-09-18T16:31:10Z`, which is `merged_at`, fifty seconds
after the commit date it used to report.

## R-7 — the unlock comment lists the pull requests the gate removed

| Time | What happened |
|---|---|
| 16:33:58 | Lock [sample-target#42](https://github.com/main-watcher-sandbox/sample-target/issues/42) opened, `lease_hours=4`, so the gate enforces it |
| 16:34:43 | [sample-target#43](https://github.com/main-watcher-sandbox/sample-target/pull/43), unlabelled, added to the merge queue |
| 16:35:5x | [Gate run 35369329229](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35369329229) **failed** on `gh-readonly-queue/main/pr-43-f19aa71…` |
| 16:36:12 | The merge queue removed #43 |
| 16:38:06 | A green result on `f19aa71` closed lock #42 |

The unlock comment:

> Tests passed on `f19aa71` ([target run](…)), so `main` is green again. Closing the lock.
>
> **Pull requests the gate removed from the merge queue while this lock was open**
>
> - #43
>
> The merge queue does not put them back, so re-queue the ones you still want merged.

The pull request is named from the gate run's queue branch,
`gh-readonly-queue/main/pr-43-<base sha>`, which is the entry the queue removed (A-7, confirmed in
the sandbox on 2026-09-16). A gate run from before the lock opened is outside the window and is not
listed.

## Restoration

- The gate workflow's token override was removed at 16:23:31; `main-watcher-gate.yml` on the target
  is the seeded template again.
- The trigger worker was scaled back to one replica at 16:38.
- #43 was closed, and the four scenario branches deleted.
- No `main-broken` issue is open, and `main` at `3adcbca` has a green `main-watcher` check run
  (16:41).
- Scenario alerts left standing in the replica for the record: main-watcher#22 ("Merged while
  locked"), #21 ("Lock lease lapsed", from issue #19) and #2 ("mention nobody").

## Not covered here

- The "Reconciliation failing" alert (ADR-015 point 7) needs a lock left unreconciled for 24 hours,
  which no scenario can produce in a sitting. It is covered by unit tests only.
- A merge whose commit subject names no pull request — a rebase-merge target — is reported as the
  commit it left on `main`. The sandbox's merge queue is set to `MERGE`, so every merge here named
  its pull request; the path is covered by unit tests only.
- The same goes for a merge whose range is longer than the 500 commits one pass reads, which is
  finished across several cycles, and for two merges stamped in the same second with one of them
  unjudgeable. Both were found by the PR #55 review rather than by the sandbox, and both are covered
  by regression tests.
