---
owner: platform-team
reviewed: 2026-09-18
review_by: 2027-03-15
---

# Manual watcher

Issue #9 supplies one Planner/Reporter cycle in `.github/workflows/watch.yml`.
It creates and completes `main-watcher` check runs. Issue #10 adds the lock issue:
a red result opens it and a green result closes it. Issue #11 adds the push list and
a comment for each later failure. Issue #12 replays interrupted reports and records human
overrides. Issue #13 gives infrastructure errors a neutral result with an alert, and #17 tests such a
head again until it gives one. #14 adds the trigger worker,
which dispatches this workflow whenever a target has work; see [worker.md](worker.md). #16 adds
the hourly backup sweep, which processes every enabled target and watches the worker in turn.
#18 stops a target run that has passed a deadline and judges it once it has stopped.
#19 renews each open lock's lease, so the gate goes on enforcing it.
#20 reconciles the merges made during a lock, following it through its closure.

Configure the `reporter` environment with `MAIN_WATCHER_APP_ID` (variable) and
`MAIN_WATCHER_PRIVATE_KEY` (secret). Install that App on each target with
Contents read, Actions write, Checks write and Issues write. Where a target's `notify`
list, or the CODEOWNERS `*` rule it falls back to, names a team, the App also needs
organisation Members read: without it the team mention renders as a team link but notifies
nobody (R-10, TS-S10). The workflow obtains a token scoped to the selected repository after
building the watcher.
Before requesting that token, `--validate-target` uses the same configuration parser
as the cycle and writes the configured owner/repository to the step outputs. Unknown,
malformed or disabled targets fail this step without requesting a token.

## Configuration

`targets.yml` is a mapping with a `targets` array. Unknown fields and duplicate
keys or repositories are rejected. An empty array is valid, and is what this repo ships:
no target is watched from here, so the hourly sweep does nothing and the trigger worker starts
no cycles. The scenario-test target belongs to the private sandbox replica, which holds the App
credentials; two watchers on one target would race for its check runs. A regression test parses
the committed file, so one the sweep could not read fails the build rather than a cycle an hour
later.

`lock_lease` sits beside `targets`, outside the list: how long, in whole minutes, a renewal keeps
an open lock enforced (ADR-014). It defaults to 240 and is at most 1440, because the gate rejects
a lease more than 24 hours ahead. ADR-014 sets it once for every target, since it is how long an
unmaintained lock may block merges — a property of the watcher, not of any one repository — so an
entry carrying its own `lock_lease` is rejected like any unknown field. The sandbox sets 10 for
TS-S7. Each entry supports:

| Field | Meaning | Default |
| --- | --- | --- |
| `repo` | Required `owner/repo` | None |
| `test_command` | Command configured in the target caller | `dotnet test --no-restore` |
| `results_glob` | Reports uploaded by the target caller | `**/TestResults/*.ctrf.json` |
| `timeout` | Test deadline, integer minutes from 1 to 340 | 30 |
| `poll_interval` | Minimum interval, positive integer minutes | 15 |
| `notify` | Handles the lock issue mentions: `user` or `org/team`, with or without `@` | `[]` |
| `enabled` | Whether the target participates | `true` |

The Planner verifies the command, glob and timeout against literal `with` values in the target's copied
`templates/main-watcher-tests.yml` before creating a check. Omitted values use the reusable
workflow's documented defaults; expressions for these settings are rejected. That caller owns test execution and secrets;
the watcher only dispatches its `sha` and `check_run_id` inputs. Its literal
`run-name: main-watcher-tests ${{ inputs.sha }}` exposes the tested SHA for the
fallback lookup; the workflow run's `head_sha` is not the tested SHA.

The C# defaults are shared between target parsing and caller validation. A regression
test reads the actual `run-integration-tests.yml` and compares its three execution
defaults, so workflow changes cannot silently drift from the validator.

## Run a cycle

```sh
gh workflow run watch.yml -f target=owner/repo
```

The first cycle creates a check and starts the target workflow. A second cycle, after the
target's `main-watcher` job completes, reports it; the trigger worker dispatches both, so a
hand-run cycle is only needed when the worker is down or for `force`. Cycles for each target
share a concurrency group and do not cancel a running cycle. The run name, `watch <target>`,
is how the worker sees whose cycle is already queued or running. Repeated dispatches do not retest a successful
or failed head. `force=true` bypasses only the three-neutral-result cap, never
an active check or the poll interval.
GitHub treats concurrency group names as case-insensitive, so differently cased
spellings of the same repository share a group, consistent with target lookup
([GitHub workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#concurrency)).

The run's first job decides which targets it acts on, and the cycle job runs once for each as a
matrix. A dispatched target is taken as given there and validated in its own leg, before any App
token is requested; a sweep reads every enabled target from `targets.yml` with the same parser
the cycles use. The concurrency group is on the cycle job, so a sweep's cycle for one target
never runs beside a dispatched cycle for it.

The reusable caller's job and literal step names determine the outcome. The
Reporter ignores unrelated jobs, reads all jobs pages from the latest attempt,
and validates and merges all JSON report files in `main-watcher-ctrf` (except
`timings.json`). It reads ZIP entries without extracting or executing files.
Missing, malformed, oversized or undownloadable reports yield “failing tests
unknown” but cannot turn a failed test step green or neutral.

A dispatch rejected with HTTP 4xx completes its check as neutral, allowing a retry
after `poll_interval` under the neutral retry rule. A lost response or missing run ID
leaves the check pending; the next cycle tries to link it without dispatching again.
The `watch.yml` log then records why the dispatch returned no run, for example
`Check 123: dispatch of main-watcher-tests.yml in owner/repo returned no run: HTTP 502 (…).`
The reason is the HTTP status, a network error or timeout message, or a response
without `workflow_run_id`. After 30 minutes, a successful lookup finding no matching run completes the check as
neutral. Ambiguous matches and failed API reads remain pending. A recovery error on
one check does not prevent reporting other pending checks, but blocks new planning
for that cycle. A target run that did start but produces no result is stopped instead;
see [stale target runs](#stale-target-runs).

Check discovery bootstraps from main's history once per `GitHubGateway` instance.
Reuse one instance per target in the worker: subsequent polls refresh the current
and previous heads, pending-check commits, and newly added commits, retaining
completed results in memory. Failed reads do not publish a partial snapshot.
Cold starts still scan history; a durable index for large-history repositories is
deferred to worker/production work rather than introducing new GitHub state in #9.
Read calls retry transient failures up to three times with bounded exponential
backoff, and collection reads follow GitHub's next-page links. Writes are not
automatically retried: recovery inspects GitHub state before another dispatch.

## Backup sweep

GitHub's scheduler is delayed and sometimes drops events, so it is only a backup (C-7,
ADR-010). `watch.yml` also runs on a schedule at minute 17, away from the congested start of
the hour. A scheduled run, or a dispatch with no `target`, is a **sweep**: it gives every
enabled target a cycle, at most five at a time, and is named `sweep` rather than
`watch <target>`, so the worker never mistakes it for one target's cycle.

```sh
gh workflow run watch.yml
```

A sweep also reports the two things only it looks for. Neither is a required write: a failure is
logged and fails the run at the end, but it never stops the cycle from reporting and testing,
which is the whole point of a sweep when the worker is down. The next sweep judges again.

- **Trigger worker appears down.** Before the cycle, the sweep asks the question the worker
  asks — does this target have work? — and how long that work has waited: since the
  `main-watcher` job completed for a report the Reporter owes, since the check run was created
  for one awaiting linking, since the 30-minute dispatch window closed for one that never got a
  target run, and, for an eligible head, since the push that made it current or since
  `poll_interval` expired, whichever is later. Work older than 15 minutes raises "Trigger worker
  appears down (work waiting on `owner/repo`)". Two kinds of work are never reported: work
  GitHub does not date, a deleted target run or a head whose push is not in the activity read;
  and work on a target the worker has dispatched a cycle for within those 15 minutes, since the
  worker is then alive and something else is stuck, which its own "reporting pending" alert
  covers (ADR-013). Reading the dispatched cycles needs `actions: read` on the watcher repo; a
  read that fails suppresses nothing, and the alert says the cycles could not be read.
- **Gate failed open.** After the cycle, the sweep looks for `main-watcher/gate-fail-open` check
  runs posted in the past hour (ADR-008 point 3). It lists the target's `main-watcher-gate.yml`
  merge-group runs, at most 50, newest first, and keeps those whose fail-open job was not
  skipped and **started** within the hour. The job is `needs: gate`, so it appears only once the
  gate job has finished: runs created up to an hour before the window are read as well, or a
  merge group gated just before a sweep would fall between two of them — too new for the first
  and too old for the second. Judging each job by its own time instead makes each sweep's window
  abut the last one's, so a fail-open is reported exactly once. One alert per sweep lists what it
  found, with a hidden marker naming the runs, so a second sweep within the same hour adds
  nothing. This is only a secondary signal: a gate that cannot reach the API usually cannot post
  that check run either, which is why merges made while a lock was open are reported by
  reconciliation instead (ADR-015, #20). A target that has not copied the gate workflow has no
  such runs.

## Lock issue

The Reporter writes the issue side first and completes the check run last (ADR-013),
so a failed issue write leaves the check `in_progress` for the next cycle.

- **Red.** If no open `main-broken` issue authored by the App exists, the Reporter
  opens one. Its body mentions the target's `notify` handles, else the owners of the
  last `*` rule in the first CODEOWNERS file on `main` (`.github/`, root, `docs/`) — a team
  handle in either place needs the App's organisation Members read to notify anyone — then
  shows the failing commit, the failing tests (or "failing tests unknown") and the target
  run. The hidden marker holds `first_red`, `lease_until` (now + `lock_lease`, read by the gate
  and renewed on every later cycle), `reported_check` and `reported_sha`, plus `last_green` when
  a green run was found.
  Test output is HTML-encoded, so it cannot mention anyone or add a second marker.
  The body stays well under GitHub's 65,536-character limit whatever the CTRF report holds:
  names, suites and messages are clipped to 200 characters, the failure list stops at
  20,000 characters with "…and N more", and at most 50 handles are mentioned. An existing
  but empty CODEOWNERS file still takes precedence over later locations, so it gives no
  owners and raises the alert.
- **Push list.** The Reporter walks `main` back up to 100 commits from the failing commit
  and takes the first whose newest `main-watcher` check run succeeded (ADR-003, ADR-017).
  It lists repository activity on `main` newer than the push that made that commit the head
  for its green run: the newest push to it at or before that run started, so a later rollback
  to the green commit is listed rather than ending the list. A push stamped up to 2 minutes
  after the run started also counts, for clock differences. If the activity read holds no
  such push (it is older than the newest 100 entries), every push read is listed and the
  issue says older pushes may also be relevant. Each row shows
  time (UTC), pusher (plain login, never `@`), type (push, force push, PR merge,
  merge-queue merge), before→after with a compare link, and the commit count from the
  compare API (`?` when GitHub cannot compare, for example after a force push). At most 100
  activity entries are read and 25,000 characters of table written. The issue says which
  source was used:
  - the last green commit, when it is in the walked history;
  - otherwise, a green check run on a commit that some listed push once made the head (the
    green commit was force-pushed away, or is older than 100 commits): activity after
    that check run started;
  - otherwise, the last 100 pushes.

  If history, check runs or activity cannot be read, the lock still opens with "Push list
  unavailable" and a link comparing the last green commit with the failing one, or, with no
  green commit known, the commits up to the failing one. Each cycle writes the walk-back
  length (commits whose check runs were read, including the activity fallback) to the log and
  the job summary, as ADR-003 requires.
- **Later red.** While an App lock is open, each further failing run adds one comment: the
  failing commit, the failing tests, the target run and a hidden `check=<id> sha=<commit>` marker. Comments
  mention nobody. Then the body's `reported_check` and `reported_sha` are pointed at that
  run. The rest of the body, including its push list, keeps the state from when the lock
  opened.
- **Green.** Every open App-authored lock gets a comment naming the green commit, marked
  `check=<id> sha=<commit> closed=green`, and is closed. The comment also lists the pull
  requests **the gate removed from the merge queue** while the lock was open, because the queue
  does not put them back and unlocking is when they are forgotten (R-7; automatic re-queueing is
  deferred, §17). They are the `merge_group` runs of `main-watcher-gate.yml` that failed since
  the issue was created, named by the pull request in each one's `gh-readonly-queue/<branch>/pr-<number>-<sha>`
  branch. A gate run list that cannot be read shortens the comment; it never keeps the lock open.
- **Author.** Only issues by the token's App (`MW_BOT_LOGIN`, `<app-slug>[bot]`) count;
  a hand-made `main-broken` issue is neither reused nor closed, matching the gate.

## Replay and overrides

A Reporter that stops part-way leaves the check run `in_progress`, so the next cycle reports
it again (ADR-013). Before any write, the Reporter lists App-authored `main-broken` issues:
every open one, and those in any state updated within `reconcile_lookback` (30 days). Each
write is skipped when its marker shows it was already made:

| Write | Skipped when |
| --- | --- |
| Open a lock | The check's ID is `reported_check` on a lock, or in a `check=` comment marker on it. If this check opened the open lock and it mentions nobody, the replay still raises that alert |
| Later-red comment | The open lock has an App comment with `check=<id>` |
| Body marker update | The open lock's `reported_check` is already this check |
| Green comment, close | The lock has an App comment with `check=<id>`; a closed lock is not in the open list |

The check run is completed last, so a replay always ends with it completed.

- **Found on a closed lock.** If this check is already on a lock that has been closed since,
  by a human or a green run, nothing is created or reopened, and the check run completes as
  `failure`.
- **An override covers its commit.** A red result creates no lock when the newest App lock
  was closed by someone other than the App and reported the commit under test: its
  `reported_sha`, or the `sha` of one of its later-failure comments. The comment counts
  because a report can stop after its comment and before the body update, and its check run
  may then end without a replay, for example as neutral after its target run was deleted.
  A failure on a different commit opens a new lock, which says the previous lock was closed
  by hand and links to it (ADR-004).
- **Duplicates.** If two App locks are open, the older is kept. Each newer one gets a comment
  marked `closed=duplicate duplicate_of=<n>` and is closed with the reason `duplicate` and
  `duplicate_issue_id` set to the kept lock's database ID. GitHub accepts the close without
  that ID, but an issue number there links an unrelated issue. A
  lock closed that way never counts as carrying a result, so a lock opened twice by a replay
  whose issue list lagged still leaves one comment on the kept lock.
- **Override comment.** After planning, each cycle looks at closed App locks updated within
  `reconcile_lookback`. A lock not closed by the App (GitHub's `closed_by`) and without an
  App comment marked `closed=override` gets one such comment, naming who closed it in plain
  text. Normally it says the merge queue is open again while `main` is still red at
  `reported_sha`, and that the watcher opens a new lock only for a failure on a different
  commit. If the App had already commented that it was closing the lock, because a report
  stopped before the close, the comment says the tests had passed at the green commit, or that
  the lock was a duplicate of the kept lock. It runs after planning, so a lock whose comments cannot be written
  (for example, a locked conversation) never stops testing; the failure still fails the
  cycle. Merge reconciliation of closed locks (ADR-015) remains #20.

### Sandbox fault injection

`MW_SANDBOX_EXIT_AFTER`, from the watcher repo's variable of that name, makes the cycle exit
with code 3 straight after the named issue write, or a comma-separated list of them. The
Reporter's writes are `create`, `comment`, `update`, `close` and `override`, and the lease's are
`renew`, `lapse` and `lapse_reported`; a replay skips the write
that was made, so it does not stop at the same point again. The check run's own completion is
`check:success`, `check:failure` and `check:neutral`, which stop the cycle after the report is
finished and before the Planner runs: `check:neutral` is ADR-017's boundary, where the retest
must survive on the rule alone (TS-S18). Set it only in the sandbox replica, and delete it
after the scenario.

## Lock lease

An open lock issue says nothing about whether anyone still maintains it, so the gate enforces one
only while its `lease_until` marker is in the future and at most 24 hours ahead (ADR-014). The
Reporter writes the first lease when it opens the lock, and every later cycle that processes the
target sets it to now + `lock_lease`, whatever else that cycle does: renewal runs before planning,
so a watcher that cannot start a test still keeps its lock enforced, while a watcher that has
stopped renews nothing and the gate lets ordinary merges through within `lock_lease` (NFR-3).
Renewal covers every open lock the App authored; a hand-made `main-broken` issue has no lease,
because the gate does not enforce one either.

The trigger worker asks for a cycle once a lease is an hour old, or halfway through `lock_lease`
where that comes sooner, so renewal never depends on the unreliable schedule (C-7); the hourly
sweep renews as a backup. The half only bites below a two-hour `lock_lease`, which in practice
means the sandbox's ten minutes, and it is what keeps a short lease from being renewed only after
it has already expired.

A renewal of a lease that had already run out is a **lapse**, and the recovery is written in this
order, so that a cycle stopped at any point resumes where it left off:

1. the same issue-body update that sets the new `lease_until` also writes
   `lapsed=<old lease_until>..<renewal time>` and the ADR-016 `sweep_required`, so a crash right
   after the renewal cannot hide that the lock was unenforced. A renewal keeps every window that
   is still owed a report and appends its own, comma-separated, so a cycle that dies before
   reporting and a second lapse after it leave both windows on the issue rather than the later
   one replacing the earlier. The marker holds at most 20 entries, so that an issue body GitHub
   would reject cannot stop the lock being renewed; past that the two oldest are **coalesced**
   into one span written `<from>..<to>*<lapses>`, never dropped, so every moment the lock went
   unenforced stays inside some recorded window and no report is lost. Reaching it needs 20
   consecutive cycles that each renewed a lapsed lease and then died;
2. one comment per owed window gives it and says the merge queue accepted unlabelled pull
   requests during it, carrying `<!-- main-watcher lapsed=<from>..<to> -->`. It says three
   separate things — what happened, what that allowed, and what is true now — because each can be
   false of a shape the others fit: a coalesced span says how many lapses it stands for rather
   than claiming one unbroken window, and a lock that has closed since is told as closed, not as
   enforced again;
3. a `watcher-infra` alert, "Lock lease lapsed on `owner/repo`", carries the same key per
   window, so ADR-012 de-duplication makes a repeat create nothing;
4. `lapse_reported=<newest renewal time>` records how far the reporting got.

While `lapse_reported` is missing or older than a window, a later cycle posts what is still
missing, and the marker on each comment keeps it from being written twice. A cycle also reports
the windows owed by locks that have **closed**, within `reconcile_lookback`, because a green run
reports and closes before the renewal runs and a person can close a lock at any time; a closed
lock gets no lease, only its report. Nothing tells the worker about that debt, so it is
discharged by the next cycle the target has for any reason, and at the latest by the hourly
sweep.

The merges made during a window are reported by [reconciliation](#reconciliation), and the merge
groups queued then have their gates re-run (#21); a lock with no readable lease at all is simply
given one, because nothing says since when the gate had been failing open.

## Reconciliation

The gate fails open on GitHub API errors and on an expired lease, a merge group can pass its gate
before a lock exists, and a person can close a lock at any moment. So a pull request without
`fixes-main` can land on a red `main`, and NFR-4 says every one of those is reported. That is
reconciliation (ADR-008, ADR-015), and it runs on every cycle, after planning, because it is a
reporting obligation rather than a testing one and it makes the most API calls of anything in a
cycle.

**Which locks.** Every App-authored `main-broken` issue that is open, or in any state and updated
within `reconcile_lookback` (30 days), whose marker does not say `reconciled=complete`.
Reconciliation therefore does **not** stop when the lock does: a human close before the watcher
has looked at a merge would otherwise mean nothing ever looks again. The App still never reopens
the issue (ADR-004).

**Which merges.** One repository-activity read per target serves every lock. For each lock, the
`pr_merge` and `merge_queue_merge` entries from `last_reconciled` — or from the issue's creation,
the first time — up to the issue's `closed_at`, or up to now while it is open. Times are compared
at one-second resolution, and a merge in the same second as the close is inside the window. Entries
after the close are ignored.

**Which pull requests.** The commits an entry added are read with the compare API, to the end of
the range, and the pull request is taken from each commit subject: `Merge pull request #N from …`
or `… (#N)`, the two shapes the gate matches. The commits-to-pull-requests API would be exact, but
it needs a Pull requests permission the `main-watcher` App does not hold, and ADR-008 promised
reconciliation would need no new one. A merge is reported **as itself** — the commit it left on
`main` — rather than passed over, when its subjects name no pull request and when GitHub can no
longer compare its range.

A pull request is named by its **last** commit, the merge or squash commit, so a range read only
part-way loses exactly the commits that would name the later pull requests. One pass therefore reads
at most 500 commits of one entry and, if any remain, **stops there**: what it read is judged, how far
it got is recorded, the entry stays in front of the cursor and the lock stays incomplete. The next
cycle carries on from that commit. What is bounded is the work one cycle does, not what is checked,
so every pull request the entry merged is still named and judged one by one.

`reconciled_commits` holds that progress for **every** entry of the second the cursor is stuck on,
as `<commit>:<commits read>` or `<commit>:done`, comma-separated. The finished ones are kept too, and
that is not tidiness: the cursor only moves a whole second at a time, so while one entry of a second
is unfinished its neighbours cannot be put behind it, and an entry whose progress was forgotten would
be read again from the start. Two long ranges stamped in one second would then take turns overwriting
each other's progress and neither would ever finish. The field is cleared once the second is behind
the cursor, and progress for an entry no longer in the window is dropped when it is read, so it
stays small.

**Which label.** The one the pull request carried **at the moment it merged**, replayed from its
`labeled` and `unlabeled` events (Issues: read) up to its `merged` event, which is in the same
timeline and so costs no second read; an event in the same second counts as before it. The merge
queue builds an entry's commit well before the group merges, so the commit's own date would judge
too early and report a label added while the entry waited; it stands in only when the timeline
holds no merge. The label it carries today is the wrong question in both directions: one
added afterwards would hide a real report, and one removed afterwards would raise a false one.

**What is written**, for each unlabelled merge, keyed by a hidden
`<!-- main-watcher merged_while_locked pr=<number> -->` (or `commit=<sha>`) so that no write is
made twice:

1. a comment on the lock, **only when it has already closed**, so its participants are notified;
2. a `watcher-infra` alert, "Merged while locked on `owner/repo`", which is the channel that does
   not depend on anyone still watching a closed issue;
3. a row under **"Merged while locked"** in the issue body, written once at the end together with
   `last_reconciled` and, for a closed lock whose whole window has been checked,
   `reconciled=complete`.

`reconciled=complete` is therefore written only after every report of that window succeeded. A
label history that cannot be read **stops the pass at that merge**: `last_reconciled` does not move
past it, nothing is marked complete, and the merge is judged on a later cycle rather than waved
through. The cursor moves **a whole second at a time**, because the next cycle reads strictly after
it: advancing between two entries stamped in the same second would put whichever was left unjudged
behind the cursor for good. The activity read is bounded at 100 entries; if it does not reach back
to the window's start, the issue says so once.

A lock whose reports cannot be **written** — a locked conversation, an issue GitHub will not update
— is the case nothing else would notice, because the writes that failed are the ones that would have
said something. So the overdue check below runs on such a lock too, before the failure is passed on;
the cycle then fails, the other locks are still reconciled, and nothing was written, so nothing
advanced.

The trigger worker asks for a cycle for a lock that has closed without being reconciled, which is
the only thing that would ask for one at all (ADR-015 point 6). A lock left unreconciled for 24
hours after closing, or a merge left unjudged that long, raises "Reconciliation failing on
`owner/repo`"; the key carries the date, so a lasting fault is one comment a day. Closures older
than `reconcile_lookback` are not revisited (R-21).

## Neutral results

The Reporter reads the latest attempt's `main-watcher` job and applies ADR-013's outcome table,
top row first:

| The job shows | Result | Alert |
| --- | --- | --- |
| The run was deleted (404) | `neutral` | "Outcome unknown on `owner/repo`" |
| More than one `main-watcher` job, none in a completed run, or `main-watcher-test` or `main-watcher-tests-finished` more than once | `neutral` | "Outcome contract broken on `owner/repo`" |
| `main-watcher-tests-finished` missing or not `success` | `neutral` | "Infrastructure error on `owner/repo`" |
| Marker `success`, test step `failure` | Red, with "failing tests unknown" when CTRF is missing or invalid | None |
| Marker `success`, test step `success` | Green | None |
| Marker `success`, test step missing or in any other state | `neutral` | "Outcome contract broken on `owner/repo`" |

A restore failure, the wrapper's deadline, a step or job timeout, a cancellation during the
tests and a lost runner all leave the marker skipped or missing, so they are infrastructure
errors. A test step renamed in the workflow leaves the marker without a test result, a
contract error; a renamed marker step reads as tests that did not finish. Anything after a
successful marker, such as a hung upload or a cancellation, does not change a red or green
result. A job still running leaves the report pending, unless it has passed one of the
[stale-run deadlines](#stale-target-runs), and any other jobs API error fails the cycle with the
check still `in_progress`.

A neutral result never creates, comments on or closes a lock. The alert comes first, then the
check run completes as `neutral` with the same explanation. Its output title records the kind:
"Outcome unknown", "Outcome contract broken" or "Infrastructure error". The Planner's own neutral
results, a rejected dispatch or no run found, are titled "Outcome unknown". Alerts for contract
errors and infrastructure errors list the job's steps and their conclusions. When an
infrastructure error follows a target's previous completed check run titled "Infrastructure
error", a second alert, "Infrastructure errors twice in a row on `owner/repo`", says so: a
restore failure caused by the code keeps `main` untested without ever locking it. A contract
error or deleted run in between ends the streak.

These alerts are the report of a neutral result, so, like a lock write, they must succeed
before the check run completes (ADR-013). If an alert or the check-run read for the streak
fails, the check run stays `in_progress`, the cycle fails, and the next cycle replays the
report. Each alert carries the check's hidden `<!-- main-watcher check=<id> -->` marker, and an
open alert with the same title that already holds it is not repeated, so the replay raises only
what is missing.

## Stale target runs

A cycle that has nothing to report looks at whether the target run should still be running
(ADR-013 point 5). Waiting for a runner is not running, so there are two deadlines, and only
one of them applies at a time:

| The `main-watcher` job | Deadline |
| --- | --- |
| Has not started (`queued`, or not yet created) | The check run's creation plus 30 minutes |
| Has started (`in_progress`) | The job's `started_at` plus the `timeout` the run was **dispatched** with, plus the 20 minutes the reusable workflow adds for setup and upload, plus a 10-minute grace |

The job's status decides which, never its `started_at`, which GitHub fills in for a queued job
too. The jobs API does not report a job's `timeout-minutes`, so the Planner records the target's
`timeout` in the check run's output when it creates it — `<!-- main-watcher timeout_minutes=30 -->`,
written by the same call, and carried forward by every later output write — and the deadline is
counted from that. It is deliberately not read from `targets.yml` each cycle: the running job
keeps the `timeout-minutes` GitHub gave it, so lowering a target's `timeout` from 120 to 30 would
otherwise cancel a healthy job at 60 minutes instead of its real 150, and raising it would delay
detection. A check run with no recorded value, from before this was written, falls back to the
current setting. The grace covers a target pinned to a workflow tag with a different margin. A started job GitHub gives no `started_at` for is never cancelled: it
may still be testing. A completed job is judged by the [outcome table](#neutral-results) instead,
whatever the deadlines say, and a deleted run gives "outcome unknown".

Past either deadline the cycle takes one step, and records it in the check run's output before
it makes the request, so a crash resumes from the recorded time rather than starting the wait
again:

1. write `cancel_requested=<time>` into the output, then cancel the run;
2. on each later cycle, ask again; 15 minutes after `cancel_requested`, write
   `force_cancel_requested=<time>` and call GitHub's force-cancel endpoint;
3. 15 minutes after that, raise "Target run could not be stopped on `owner/repo`" and keep
   force-cancelling every cycle.

The check run stays `in_progress` throughout, which is what blocks a second test: the shared
eligibility rule refuses any head while a check run is active, so a stale run stops the retry,
a newer head and a forced dispatch alike, until the run stops or someone deletes it (R-24). The
output title is "Stopping a stale target run" meanwhile. As soon as the job completes, the
outcome table judges its steps like any other: a cancellation during the tests leaves no
`main-watcher-tests-finished` marker and is an infrastructure error, while a run stopped after
the marker succeeded keeps its red or green result. GitHub can take several minutes to tear a
cancelled job down, and the check run waits for it.

**Releasing a run GitHub will not stop.** ADR-013 says a person can delete the run, which gives
"outcome unknown" and releases the target. In the sandbox GitHub answered `403 Could not delete
the workflow run` while the run was still going, so the action is two steps: **cancel the run by
hand, then delete it once it has stopped**. Cancelling alone is usually enough — the job is then
judged from its steps like any other. Deleting matters only when those steps should not be judged
at all. ADR-013's one-step wording needs an amending ADR.

A cancel or force-cancel GitHub refuses is logged with its status and never throws: the run is
asked again next cycle, and the 15-minute steps are the escalation. An error reading the jobs
API changes nothing, since nothing is written before the read; the cycle exits non-zero and the
next one looks again. The alert carries an `<!-- main-watcher unstoppable check=<id> -->` marker,
so the cycles that keep force-cancelling raise it only once.

### Sandbox fault injection

Two watcher-repo variables support TS-S16 (g) and (h) and are unset in production:

| Variable | Effect |
| --- | --- |
| `MW_QUEUE_DEADLINE_MINUTES` | The queue deadline, 1 to 30 minutes; anything else falls back to 30. The trigger worker reads the same variable and **must be given the same value**, or it starts cycles for runs the Planner does not judge stale |
| `MW_SANDBOX_REFUSE_CANCEL` | `true` makes every cancel and force-cancel fail without asking GitHub, so the "could not be stopped" alert can be reached without a run GitHub genuinely cannot stop |

## Retesting a neutral head

A `neutral` result means the head was not tested, so it stays eligible: once `poll_interval`
has passed since that check run completed, the next cycle starts a new check run on the same
commit (ADR-017). There is no separate retry step. Completing the check run as `neutral` is the
whole of the failure handling, and the retest follows from the rule, so a crash anywhere after
that write cannot lose it. Each attempt has its own check run, and a commit's result is its
newest one; the Reporter's walk-back for the last green commit reads them the same way.

The Planner reads the check runs itself, immediately before creating a new one and inside
`watch.yml`'s per-target concurrency group, so two cycles cannot both start the same retest.

After three `neutral` check runs on one head, with no result since, the head stops being
eligible and the Planner raises "Head untestable on `owner/repo`", naming the commit and any
open App lock, which can no longer close on its own (R-23). The alert carries a hidden
`<!-- main-watcher untestable sha=<sha> -->` marker, so later cycles say nothing more about the
same head; a head that runs out later comments on the same alert. Testing resumes when a push
creates a new head, or when `watch.yml` is dispatched with `force: true`, which ignores only
this cap: a forced dispatch still waits for an active check run and for `poll_interval`, and
never retests a head whose newest result is `success` or `failure`.

## Alerts

`watch.yml` raises `watcher-infra` issues in its own repository with the workflow's
`GITHUB_TOKEN` (`issues: write`), since the App token is scoped to the target. An open
alert with the same title gets a comment instead of a new issue (ADR-012). A lock that
mentions nobody raises "Lock issues on `owner/repo` mention nobody". Neutral results raise the
alerts [above](#neutral-results), a target run GitHub will not stop raises the one
[above](#stale-target-runs), a head out of attempts raises the one
[above](#retesting-a-neutral-head), a lock that went unenforced raises the one
[above](#lock-lease), reconciliation raises the two [above](#reconciliation), and a sweep raises
the two [above](#backup-sweep). A failed
"mention nobody", "target run could not be stopped" or "head untestable" alert never blocks the
lock, the check run or the testing the cycle does; the cycle logs it and exits non-zero. A failed
neutral-result alert leaves the check run `in_progress` for a replay, a failed "lock lease
lapsed" alert leaves `lapse_reported` unwritten, and a failed "Merged while locked" alert leaves
`last_reconciled` where it was, so a later cycle raises each of them.

## Validation

Run `dotnet test` and the CI-pinned actionlint. The core suite includes real xUnit
reports and a GitHub reusable-caller jobs response, with provenance in its fixtures
directory. Sandbox execution uses the private watcher replica described in
`sandbox/README.md`. The [issue #9 validation record](../sandbox/issue-9-validation.md)
links the passing and failing checks, their Planner and Reporter cycles, and
the target restoration evidence. The [issue #10 validation record](../sandbox/issue-10-validation.md)
covers a real lock opening and closing, and TS-S4 with that lock. The
[issue #11 validation record](../sandbox/issue-11-validation.md) covers TS-S2: the push list
and the later-failure comment. The [issue #12 validation record](../sandbox/issue-12-validation.md)
covers TS-S14 (a), (b) and (d) and the override part of TS-S3. The
[issue #13 validation record](../sandbox/issue-13-validation.md) covers TS-S16 (a) to (f). The
[issue #20 validation record](../sandbox/issue-20-validation.md) covers TS-S9 and TS-S15: a gate
failing open on a 401 and on an expired lease, unlabelled merges, a human close before recovery, and
what the next cycle reported. The
[issue #16 validation record](../sandbox/issue-16-validation.md) covers TS-S11: with the worker
scaled to zero, a sweep tested a push that had waited two hours and raised "trigger worker
appears down", and a merge group whose gate met an expired lease was reported once. It also
records what the schedule did: GitHub dropped two of the cron's first three slots and ran the
third two minutes late, which is C-7 measured rather than assumed. The
[issue #17 validation record](../sandbox/issue-17-validation.md) covers TS-S12 and TS-S18: a
cancelled run retested on the same head, three neutral results reaching the cap, the "head
untestable" alert, and a forced dispatch testing the head again. The
[issue #18 validation record](../sandbox/issue-18-validation.md) covers TS-S16 (g) and (h): the
queue deadline that stops applying once a job starts, a job that never got a runner cancelled and
judged, a hanging job cancelled at its run deadline whose lock still opened, and a run whose
cancels were refused, which raised "target run could not be stopped" and stopped every test on
the target — including a forced dispatch — for 78 minutes.
