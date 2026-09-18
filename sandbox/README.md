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
| `issue-53-validation.md` | The replica's CI green on its own sandbox target list, once `CommittedTargetListParses` scoped its sandbox-target clause to this repo, for #53 |
| `sample-target/` | Template for the synthetic target repos. Its [README](sample-target/README.md) lists the `sandbox.json` switches |
| `rulesets/main-merge-queue.json` | The merge-queue ruleset applied to `main` in each sandbox target |
| `publish-public.sh` | Publishes the gate action, the reusable test workflow and their .NET projects to the public `main-watcher-sandbox/gate` repo |
| `upload-switches.yml` | The `fail_upload`, `hang_upload` and `hang_upload_forever` steps that `publish-public.sh` inserts into the sandbox build of the test workflow |
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

`main-watcher-sandbox/main-watcher` is private and stands in for this repo. Its `reporter`
environment holds the `main-watcher` App key. Before a scenario test, put the MainWatcher tree
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
pointing its caller's `uses:` at that branch (and back to `@main` afterwards):

| Branch | Change | Scenario |
|---|---|---|
| `ts-s16-renamed-step` | The test step is named `main-watcher-tests-renamed` | TS-S16 (c) |
| `ts-s16-step-timeout` | The test step has `timeout-minutes: 1` | TS-S16 (e) |
| `ts-s16-report-stuck` | The `report` job runs on `sandbox-no-such-runner`, which no runner has | TS-S16 (f) |

A run on `ts-s16-report-stuck` stays queued until it is cancelled. `publish-public.sh` does not
update these branches; re-create them from `main` if the published workflow changes.

## Stale-run switches

For TS-S16 (g) and (h), two replica variables steer the ADR-013 stale-run lifecycle. Set both
only for the scenario and delete them afterwards; `MW_QUEUE_DEADLINE_MINUTES` must also be set on
the trigger worker, or it starts cycles for runs the Planner does not judge stale.

```
gh variable set MW_QUEUE_DEADLINE_MINUTES -R main-watcher-sandbox/main-watcher --body 5
kubectl -n main-watcher-sandbox set env deploy/trigger-worker MW_QUEUE_DEADLINE_MINUTES=5
gh variable set MW_SANDBOX_REFUSE_CANCEL -R main-watcher-sandbox/main-watcher --body true
```

`MW_QUEUE_DEADLINE_MINUTES` shortens the 30-minute queue deadline to 1–30 minutes.
`MW_SANDBOX_REFUSE_CANCEL=true` makes every cancel and force-cancel fail without asking GitHub,
so the "target run could not be stopped" alert can be reached in 30 minutes rather than by
finding a run GitHub genuinely cannot stop.

The run deadline has no switch: it is the job's `started_at` plus the target's `timeout`, the
workflow's 20-minute margin and a 10-minute grace, so the shortest one a scenario can arrange is
31 minutes after the job starts. GitHub's own job timeout normally ends a job first, so to reach
the run deadline the target's `timeout` in the replica's `targets.yml` is lowered **after** the
run has started: the running job keeps the `timeout-minutes` it was created with, and the watcher
then judges it against the smaller one, which is the lost-runner case the grace exists for.

A job that never gets a runner is arranged by adding `runs-on: sandbox-no-such-runner` to the
target caller's `with:` block. The caller validator checks only `test-command`, `results-glob`
and `timeout-minutes`, so the extra input is accepted.

## Reporter fault switch

For TS-S14, set the replica's `MW_SANDBOX_EXIT_AFTER` variable to the issue writes to
stop after (`create`, `comment`, `update`, `close`, `override`, comma-separated). The cycle
exits right after that write, leaving the check run `in_progress`; the next cycle replays
the report. The lease's writes have names too — `renew`, `lapse` and `lapse_reported` — which is
how TS-S17 (b) stops a cycle between a renewal and its queue sweep.

The check run's own completion has names too: `check:success`, `check:failure` and
`check:neutral`. These stop the cycle after the report is finished, not part-way through it, so
`check:neutral` kills a cycle at the exact moment ADR-017 relies on — the neutral is written and
nothing else has run (TS-S18).

Delete the variable afterwards:

```
gh variable set MW_SANDBOX_EXIT_AFTER -R main-watcher-sandbox/main-watcher --body create
gh variable delete MW_SANDBOX_EXIT_AFTER -R main-watcher-sandbox/main-watcher
```
