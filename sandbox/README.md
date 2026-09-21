# Sandbox

Material for the scenario-test sandbox, the `main-watcher-sandbox` organisation (TS-001 §3).

| Path | What it is |
|---|---|
| `issue-9-validation.md` | Passing/failing watcher checks and target restoration evidence for #9 |
| `issue-10-validation.md` | A real lock opened and closed, TS-S4 with that lock, and restoration evidence for #10 |
| `issue-11-validation.md` | TS-S2: three quick pushes during a slow run, the lock's push list and the later-failure comment, for #11 |
| `issue-12-validation.md` | TS-S14 (a), (b) and (d) with the Reporter fault switch, and the override part of TS-S3, for #12 |
| `issue-13-validation.md` | TS-S16 (a) to (f): neutral results, their alerts, and red results that survive upload failures and late cancels, for #13 |
| `issue-14-validation.md` | The trigger worker's sandbox deployment and its TS-S1 and TS-S2 evidence, for #14 |
| `issue-15-validation.md` | TS-S14 (c): the worker's "reporting pending" alert with the Reporter's issue writes failing for 20 minutes, for #15 |
| `issue-16-validation.md` | TS-S11: a push tested by a sweep with the worker scaled to zero, the "worker appears down" and gate fail-open alerts, and what GitHub's scheduler actually did, for #16 |
| `issue-17-validation.md` | TS-S12 and TS-S18: a cancelled run retested on the same head, three neutral results reaching the cap, the "head untestable" alert and a forced dispatch, for #17 |
| `issue-18-validation.md` | TS-S16 (g) and (h): the queue and run deadlines, the cancel and force-cancel lifecycle and the "could not be stopped" alert, for #18 |
| `issue-19-validation.md` | TS-S7: merges through a watcher outage with and without a lock, the "LOCK LEASE EXPIRED" gate, and the renewal, lapse record, comment and alert on recovery, for #19 |
| `issue-20-validation.md` | TS-S9 and TS-S15: a gate failing open on a 401 and on an expired lease, unlabelled merges, a human close before recovery, and the reports the next cycle made, for #20 |
| `issue-21-validation.md` | TS-S17: groups queued before a lock re-checked by the queue sweep, a gate still running, a crash right after a lease renewal, and the merge nothing could stop, for #21 |
| `issue-22-validation.md` | TS-S13, check-run half: suite time, the change from the last green run, the 5 slowest tests and the retry flag, against the CTRF artifact, for #22 |
| `issue-23-validation.md` | TS-S10: whether an App's team @-mention notifies, the organisation `Members: read` it needs, and the CODEOWNERS fallback, for #23 |
| `issue-24-validation.md` | The TS-001 §6 checklist as tests, a target test run that inspects its own environment for Main Watcher keys, and how to run TS-S8, for #24 |
| `issue-25-validation.md` | The scenario suite's first runs: what they found and how long they took, for #25 |
| `issue-53-validation.md` | The replica's CI green on its own sandbox target list, once `CommittedTargetListParses` scoped its sandbox-target clause to this repo, for #53 |
| `sample-target/` | Template for the synthetic target repos. Its [README](sample-target/README.md) lists the `sandbox.json` switches |
| `rulesets/main-merge-queue.json` | The merge-queue ruleset applied to `main` in each sandbox target |
| `publish-public.sh` | Publishes the gate action, the reusable test workflow and their .NET projects to the public `main-watcher-sandbox/gate` repo |
| `upload-switches.yml` | The `fail_upload`, `hang_upload` and `hang_upload_forever` steps that `publish-public.sh` inserts into the sandbox build of the test workflow |
| `ts-s8-credential-scope.sh` | TS-S8: proves `mw-observer` and `mw-doorbell` are refused (403) outside their scope, and checks where the keys live, each App's installed repositories (R-11), who can read the worker's Secret and that nothing exposes the worker inbound |
| `run-scenarios.sh`, `scenarios/` | The scenario suite: TS-S1 to TS-S18 from one entry point, which a release requires. See "The scenario suite" below |
| `seed-target.sh` | Pushes the template and the gate and test workflows to a sandbox repo, creates the `main-broken` and `fixes-main` labels, and applies the ruleset. Re-run it to reset a repo |

Seed or reset both targets (needs `gh` logged in as a sandbox org admin):

```
sandbox/seed-target.sh main-watcher-sandbox/sample-target
sandbox/seed-target.sh main-watcher-sandbox/sample-target-slow --slow
```

To check a seeded repo builds and writes CTRF, run its `sandbox-selftest` workflow:

```
gh workflow run sandbox-selftest.yml -R main-watcher-sandbox/sample-target
```

## The sandbox watcher and gate repos

`main-watcher-sandbox/main-watcher` stands in for this repo. It is public, like every sandbox repo, since 2026-09-21. On
the Free plan only private repos use the org's 2000 Actions minutes a month, and one full scenario suite run used about
700 of them in the replica (#25). Its `reporter` environment holds the `main-watcher` App key; secrets are never shown
on a public repo. Before a scenario test, put the MainWatcher tree
under test on its `main`.

The replica's `main` is never an ancestor of that commit: it ends in the `targets.yml` commit
below, which exists only there. So a plain `git push HEAD:main` is rejected as a non-fast-forward.
Take the tree across with a merge commit instead, which is what the replica's history is made of
— `Sandbox: take MainWatcher at <sha>`, one per scenario. Run this from the commit under test:

```
replica=https://github.com/main-watcher-sandbox/main-watcher.git
git fetch "$replica" main
message="Sandbox: take MainWatcher at $(git rev-parse --short HEAD)"
take="$(git commit-tree "HEAD^{tree}" -p FETCH_HEAD -p HEAD -m "$message")"
git push "$replica" "$take:main"
```

The merge keeps the replica's own history, and its tree is the tree under test exactly: the
`targets.yml` commits it carried are its ancestors, not its content. So the push overwrites the
replica's `targets.yml` with this repo's, which lists **no** targets:
the watcher repo watches nothing of its own, and two watchers on one target would race for its
check runs. So after every such push, set the replica's list back to the sandbox target. Its
`poll_interval` of 1 minute is what makes a scenario take minutes rather than a quarter of an
hour, and `notify` keeps every lock from raising "Lock issues on … mention nobody": the target
has no CODEOWNERS, so with an empty list that alert returns with each new lock:

```
gh api -X PUT repos/main-watcher-sandbox/main-watcher/contents/targets.yml \
  -f message='Sandbox: watch sample-target' \
  -f sha="$(gh api repos/main-watcher-sandbox/main-watcher/contents/targets.yml --jq .sha)" \
  -f content="$(base64 -w0 <<'YAML'
# A 10-minute lock_lease, so a lapse takes minutes rather than four hours (TS-S7).
lock_lease: 10
targets:
  - repo: main-watcher-sandbox/sample-target
    test_command: dotnet test --no-restore
    results_glob: '**/TestResults/*.ctrf.json'
    timeout: 30
    poll_interval: 1
    notify: [pat-actium]
    enabled: true
YAML
)"
```

Sandbox targets are public, because the sandbox org is on the Free plan, where the merge
queue works only in public repos. A public repo cannot use an action or reusable workflow
from a private one, so the gate and the reusable test workflow are also published on their
own to the public `main-watcher-sandbox/gate` repo, and seeded targets use its `@main`:

```
sandbox/publish-public.sh
```

To test a commit by hand, as the watcher will, dispatch the target's `main-watcher-tests`
workflow. The run's `main-watcher-ctrf` artifact holds the CTRF reports and `timings.json`, and
its `report` job's summary shows the slowest tests and duration trends:

```
gh workflow run main-watcher-tests.yml -R main-watcher-sandbox/sample-target -f sha=<commit>
```

Set `GATE_REF` when seeding to pin another ref.

## The sandbox trigger worker

The worker runs in the cluster's `main-watcher-sandbox` namespace and watches
`main-watcher-sandbox/main-watcher`. Build the image, create the key Secret once, and apply
the overlay as [docs/worker.md](../docs/worker.md) describes. Scale it to zero for the
scenarios that need the worker down (TS-S7, TS-S11):

```
kubectl -n main-watcher-sandbox scale deploy/trigger-worker --replicas=0
```

## Hand-made locks

Scenario tests that need a hand-made lock (TS-S4, TS-S5) open one with the
`sandbox-lock` workflow. It creates a `main-broken` issue authored by `main-watcher[bot]`
with a `lease_until` marker, or closes the open ones:

```
gh workflow run sandbox-lock.yml -R main-watcher-sandbox/main-watcher -f target=sample-target -f action=open -f lease_hours=4
gh workflow run sandbox-lock.yml -R main-watcher-sandbox/main-watcher -f target=sample-target -f action=close
```

A negative `lease_hours` makes an expired lease, for the "LOCK LEASE EXPIRED" path.

## Workflow variants

TS-S16 needs test workflows that differ from the published one. Each is a branch of
`main-watcher-sandbox/gate`, made from its `main` with one change, and a target uses one by
pointing its caller's `uses:` at that branch (and back to `@main` afterwards).
`publish-public.sh` publishes them with `main`, so they never fall behind it:

| Branch | Change | Scenario |
|---|---|---|
| `ts-s16-renamed-step` | The test step is named `main-watcher-tests-renamed` | TS-S16 (c) |
| `ts-s16-step-timeout` | The test step has `timeout-minutes: 1` | TS-S16 (e) |
| `ts-s16-report-stuck` | The `report` job runs on `sandbox-no-such-runner`, which no runner has | TS-S16 (f) |
| `ts-s16-held-job` | The test job waits in the concurrency group `sandbox-hold-<repo>`, which the target's `sandbox-hold` workflow holds for as many minutes as it is asked | TS-S16 (g) |
| `ts-s16-long-timeout` | The test job has `timeout-minutes: 120`, so a job that never ends reaches Main Watcher's run deadline first | TS-S16 (h) |

A run on `ts-s16-report-stuck` stays queued until it is cancelled.

## Sandbox switches

Replica variables steer the watcher's fault injection, and the trigger worker's environment
carries the one setting it shares. Production sets none of them. Each takes a comma-separated
list. An entry written `owner/repo=value` applies to that target only; a bare one applies to
every target (`SandboxSwitch`). That is what lets the scenario suite fault one target while
others run.

| Variable | Values | Effect | Scenario |
|---|---|---|---|
| `MW_SANDBOX_EXIT_AFTER` | Write names, below | The cycle exits right after the named write, leaving the rest for the next cycle | TS-S14, TS-S17 (b), TS-S18 |
| `MW_SANDBOX_REFUSE_CANCEL` | `true` | Every cancel and force-cancel fails without asking GitHub | TS-S16 (h) |
| `MW_QUEUE_DEADLINE_MINUTES` | 1 to 30 | Shortens the 30-minute ADR-013 queue deadline | TS-S16 (g) |
| `MW_SANDBOX_READ_ONLY_ISSUES` | A plain list of targets | Their cycles get an App token that can only read issues, so every lock write fails with a real 403 | TS-S14 (c) |

```
gh variable set MW_SANDBOX_EXIT_AFTER -R main-watcher-sandbox/main-watcher --body main-watcher-sandbox/sample-target=create
gh variable delete MW_SANDBOX_EXIT_AFTER -R main-watcher-sandbox/main-watcher
```

`MW_QUEUE_DEADLINE_MINUTES` must be given to the trigger worker too, or it starts cycles for runs
the Planner does not judge stale. The worker reads it only at start-up, so the sandbox overlay
sets it once, for `sample-target-10` alone, and the scenario suite sets the replica variable to
match.

**Write names.** The Reporter's issue writes are `create`, `comment`, `update`, `close` and
`override`. The lease's are `renew`, `lapse` and `lapse_reported`, which is how TS-S17 (b) stops
a cycle between a renewal and its queue sweep. The check run's completion has names too:
`check:success`, `check:failure` and `check:neutral`. These stop the cycle after the report is
finished, not part-way through it. So `check:neutral` kills a cycle at the exact moment ADR-017
relies on: the neutral is written and nothing else has run (TS-S18).

**`MW_SANDBOX_REFUSE_CANCEL`** lets the "target run could not be stopped" alert be reached in
30 minutes. Otherwise it would need a run GitHub genuinely cannot stop.

**The run deadline** has no switch. It is the job's `started_at`, plus the `timeout` the check
run recorded when the run was dispatched, the workflow's 20-minute margin and a 10-minute grace.
A scenario reaches it by using a 2-minute target timeout on the `ts-s16-long-timeout` variant,
whose job GitHub would not end for two hours. The deadline then falls 32 minutes after the job
starts.

A job that never gets a runner is arranged by adding `runs-on: sandbox-no-such-runner` to the
target caller's `with:` block. The caller validator checks only `test-command`, `results-glob`
and `timeout-minutes`, so the extra input is accepted.

## The scenario suite

`run-scenarios.sh` runs TS-S1 to TS-S18 against the sandbox from one entry point, and a release
requires it (TS-001 §5, [docs/release.md](../docs/release.md)). Run it from a clean checkout of
the commit under test, on the machine whose cluster runs the sandbox worker:

```
sandbox/run-scenarios.sh                    # everything; a pass posts the scenario-suite status
sandbox/run-scenarios.sh --only TS-S13      # one scenario; posts scenario-suite/ts-s13
sandbox/run-scenarios.sh --list             # the units, their phase and rough length
```

It first puts the commit into the sandbox:

- it runs `publish-public.sh`;
- it pushes the tree to the replica, as above;
- it builds the worker image and rolls it out;
- it runs `seed-target.sh` on every pool target.

It then configures the sandbox for the run:

- it writes the replica's `targets.yml` for the pool: `lock_lease: 10`, `poll_interval: 1`, and
  `notify` set to the person running it;
- it clears the switches;
- it closes open alerts;
- it resets every target to green.

With `--no-deploy` it skips the first part and tests whatever the sandbox already runs.

A full run takes about 1 h 45 min. The longest unit, TS-S16 (h)'s unstoppable run, starts first and sets that time: from
its run deadline through the refused cancel and force-cancel to the alert is about 90 min of GitHub time.

**The pool.** Scenarios run side by side, each on a target of its own: `sample-target` and
`sample-target-2` to `sample-target-10`. A pool target needs three things:

- it is seeded from the template;
- `main-watcher` and `mw-observer` are installed on it;
- the `sandbox-owners` team has write access (TS-S10).

`--targets N` uses fewer targets.

After each unit, its target is reset:

- its pull requests are closed;
- its switches and its `targets.yml` entry are cleared;
- its files are put back as seeded;
- its runs are stopped;
- `main` is green and no lock is open;
- its closed locks are reconciled;
- the alerts about it are closed.

**The outage.** TS-S7, TS-S11, TS-S15 and TS-S17 (b) need the watcher down. There is only one
worker and one hourly schedule, so they share one outage:

1. Each opens its lock while the worker runs.
2. The worker is scaled to zero and `watch.yml` disabled, so GitHub's schedule cannot start a
   sweep.
3. Each does its part while the watcher is down.
4. Once all are done, `watch.yml` is enabled. One sweep is dispatched and awaited: it is the
   backup that should notice the worker is down.
5. Only then is the worker started again.

The two TS-S16 (h) units start at the same time as the outage units: their 32-minute wait for
the run deadline needs no worker. Other units that need no worker run during the outage; the rest
wait for it to end.

**Output.** Everything goes to `sandbox/scenarios/out/<time>-<commit>/`:

- `report.md`: what each unit checked and saw;
- `results.json`;
- the log of the suite, of each unit and of each target;
- TS-S8's table.

The exit code is 0 only if every unit passed.

**Commit statuses.** A full run posts `scenario-suite` on the commit, `success` or `failure`. It
does so only when the commit is pushed to GitHub and the working tree has no uncommitted changes.
Any run that includes TS-S13 posts `scenario-suite/ts-s13`. `--no-status` posts neither.

**What it cannot check.**

- The CTRF job summary is not in the API. TS-S13's job-summary half is checked only as far as the
  `report` job succeeding and saving its history artifact. Its check-run half is checked against
  the CTRF reports and `timings.json`.
- TS-S18's "once the feed is back" needs a new head, because the feed switch is a file.
