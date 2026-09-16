---
owner: platform-team
reviewed: 2026-09-16
review_by: 2027-03-15
---

# Manual watcher

Issue #9 supplies one Planner/Reporter cycle in `.github/workflows/watch.yml`.
It creates and completes `main-watcher` check runs. Issue #10 adds the lock issue:
a red result opens it and a green result closes it. Issue #11 adds the push list and
a comment for each later failure. Replay, lease renewal, stale-run cancellation and the
automatic trigger are separate backlog items.

Configure the `reporter` environment with `MAIN_WATCHER_APP_ID` (variable) and
`MAIN_WATCHER_PRIVATE_KEY` (secret). Install that App on each target with
Contents read, Actions write, Checks write and Issues write. The workflow obtains a token
scoped to the selected repository after building the watcher.
Before requesting that token, `--validate-target` uses the same configuration parser
as the cycle and writes the configured owner/repository to the step outputs. Unknown,
malformed or disabled targets fail this step without requesting a token.

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

The first cycle creates a check and starts the target workflow. Dispatch again
after the target's `main-watcher` job completes to report it. Until the worker
is implemented, reporting is manual. Cycles for each target share a concurrency group and
do not cancel a running cycle. Repeated dispatches do not retest a successful
or failed head. `force=true` bypasses only the three-neutral-result cap, never
an active check or the poll interval.
GitHub treats concurrency group names as case-insensitive, so differently cased
spellings of the same repository share a group, consistent with target lookup
([GitHub workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#concurrency)).

The reusable caller's job and literal step names determine the outcome. The
Reporter ignores unrelated jobs, reads all jobs pages from the latest attempt,
and validates and merges all JSON report files in `main-watcher-ctrf` (except
`timings.json`). It reads ZIP entries without extracting or executing files.
Missing, malformed, oversized or undownloadable reports yield “failing tests
unknown” but cannot turn a failed test step green or neutral.

A dispatch rejected with HTTP 4xx completes its check as neutral, allowing a retry
after `poll_interval` under the neutral retry rule. A lost response or missing run ID
leaves the check pending; the next cycle tries to link it without dispatching again.
After 30 minutes, a successful lookup finding no matching run completes the check as
neutral. Ambiguous matches and failed API reads remain pending. A recovery error on
one check does not prevent reporting other pending checks, but blocks new planning
for that cycle. Automated cancellation and infrastructure alerts remain in #13 and #18.

Check discovery bootstraps from main's history once per `GitHubGateway` instance.
Reuse one instance per target in the worker: subsequent polls refresh the current
and previous heads, pending-check commits, and newly added commits, retaining
completed results in memory. Failed reads do not publish a partial snapshot.
Cold starts still scan history; a durable index for large-history repositories is
deferred to worker/production work rather than introducing new GitHub state in #9.
Read calls retry transient failures up to three times with bounded exponential
backoff, and collection reads follow GitHub's next-page links. Writes are not
automatically retried: recovery inspects GitHub state before another dispatch.

## Lock issue

The Reporter writes the issue side first and completes the check run last (ADR-013),
so a failed issue write leaves the check `in_progress` for the next cycle.

- **Red.** If no open `main-broken` issue authored by the App exists, the Reporter
  opens one. Its body mentions the target's `notify` handles, else the owners of the
  last `*` rule in the first CODEOWNERS file on `main` (`.github/`, root, `docs/`), then
  shows the failing commit, the failing tests (or "failing tests unknown") and the target
  run. The hidden marker holds `first_red`, `lease_until` (now + 4 h, read by the gate),
  `reported_check` and `reported_sha`, plus `last_green` when a green run was found.
  Lease renewal comes with #19 and replay against closed locks with #12.
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
  failing commit, the failing tests, the target run and a hidden `check=` marker. Comments
  mention nobody. The body, including its push list, keeps the state from when the lock
  opened; updating it comes with replay (#12).
- **Green.** Every open App-authored lock gets a comment naming the green commit and is
  closed.
- **Author.** Only issues by the token's App (`MW_BOT_LOGIN`, `<app-slug>[bot]`) count;
  a hand-made `main-broken` issue is neither reused nor closed, matching the gate.

## Alerts

`watch.yml` raises `watcher-infra` issues in its own repository with the workflow's
`GITHUB_TOKEN` (`issues: write`), since the App token is scoped to the target. An open
alert with the same title gets a comment instead of a new issue (ADR-012). A lock that
mentions nobody raises "Lock issues on `owner/repo` mention nobody". A failed alert
never blocks the lock or the check run; the cycle logs it and exits non-zero.

## Validation

Run `dotnet test` and the CI-pinned actionlint. The core suite includes real xUnit
reports and a GitHub reusable-caller jobs response, with provenance in its fixtures
directory. Sandbox execution uses the private watcher replica described in
`sandbox/README.md`. The [issue #9 validation record](../sandbox/issue-9-validation.md)
links the passing and failing checks, their Planner and Reporter cycles, and
the target restoration evidence. The [issue #10 validation record](../sandbox/issue-10-validation.md)
covers a real lock opening and closing, and TS-S4 with that lock. The
[issue #11 validation record](../sandbox/issue-11-validation.md) covers TS-S2: the push list
and the later-failure comment.
