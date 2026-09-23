---
owner: platform-team
reviewed: 2026-09-23
review_by: 2027-03-15
---

# Trigger worker

`src/MainWatcher.Worker` is the self-hosted .NET service that starts `watch.yml` when a
target has work (ADR-010). It is a `BackgroundService` beside a single HTTP endpoint,
`/healthz`, for the Kubernetes liveness probe. It makes outbound HTTPS calls only, and
needs no inbound Service or Ingress.

Issues #14 and #15 cover the cycle and the alerts described here, and #16 the hourly backup
sweep, which does this work when the worker is not and says so; see
[watcher.md](watcher.md#backup-sweep). #18 adds the stale-run deadlines below, #19 the lock
lease, #20 the closed lock that still owes reconciliation and #21 the unfinished queue sweep.

## A cycle

Every `check_period` (default 60 s), the worker:

1. reads `targets.yml` from the watcher repo's `main` through `mw-observer`;
2. reads `watch.yml` runs that are still queued or running, and skips those targets: such a
   run acts on the current state, so another dispatch would only queue a cycle with nothing
   left to do. `watch.yml`'s `run-name` is `watch <owner/repo>`, which is how the worker
   tells whose cycle it is. When that read fails, no target is skipped, because a duplicate
   dispatch is harmless (ADR-010). A run that has not started by its deadline is stopped
   first ([below](#a-cycle-that-does-not-start)), since until it completes it blocks its
   target;
3. for each enabled target, looks for work (below) through that target's `mw-observer`
   gateway, reusing one `GitHubGateway` per target so its check-run snapshot is kept;
4. starts `watch.yml` once for each target with work, through `mw-doorbell`, passing
   `target`. The dispatch POST is never retried: a lost response may still have started the
   run, and the next cycle looks again.

A target whose reads fail is logged and counted; the other targets are still processed.

### A cycle that does not start

A `watch.yml` run for a target that is still not `in_progress` 20 minutes after it was
created — `waiting` at an environment gate, or `queued`, `pending` or `requested` — blocks
every later cycle for that target (ADR-020). Scenario suite run 11 found one held at the
`reporter` gate for an approval nobody could give (MainWatcher#67). The worker stops it as the
Planner stops a target run (ADR-013 point 5):

| Time after the deadline | The worker |
| --- | --- |
| 0 | cancels the run through `mw-doorbell`, and raises "`watch.yml` run stuck `<state>` on `owner/repo`" |
| 15 min | force-cancels it |
| 30 min | force-cancels it on each cycle, and raises "`watch.yml` run could not be stopped on `owner/repo`" |

The deadline is the run's `created_at` plus 20 minutes, plus the environment's wait timer for a
`waiting` run, so every step follows from GitHub's own times and a restart resumes where it
was. Before stopping a `waiting` run, the worker reads its `pending_deployments` through
`mw-observer`. When a gate lists reviewers, a person can approve it, so the run is left alone
and "`watch.yml` run waiting for a reviewer on `owner/repo`" names them; the `reporter`
environment must have none ([watcher.md](watcher.md)). When that read fails, nothing is
cancelled and the failure counts towards "cycles keep failing". Once the run has completed,
the next cycle dispatches again, as often as it takes. A `sweep` run names no target and is
never stopped by this rule; a stuck one holds each target's concurrency group, so the targets'
own runs show up here as stuck `pending` until a person cancels the sweep. All
GitHub calls go through `GitHubGateway` (§9 of ARCH-001), including the App and installation
token calls.

### What counts as work

For each in-progress `main-watcher` check run on the target, oldest first:

| The check run shows | Work | Why |
| --- | --- | --- |
| A target run whose `main-watcher` job has completed | Yes | The Reporter can report it, whatever the run's other jobs are doing (ADR-013) |
| A target run that no longer exists (404) | Yes | The Reporter records "outcome unknown" |
| A completed target run with no `main-watcher` job | Yes | The Reporter records the broken contract |
| A target run whose `main-watcher` job is still queued or running, within its deadlines | No | Nothing to report yet |
| A job that has not started 30 minutes after the check run was created | Yes | `Planner.Stop` cancels the run (ADR-013 point 5) |
| A started job past its `started_at` plus the `timeout` its run was dispatched with, the workflow's 20-minute margin and a 10-minute grace | Yes | The same |
| A run already asked to stop, until its job has completed | Yes | The Planner asks again, force-cancels and finally alerts |
| No `external_id`, and exactly one matching target run | Yes | `Planner.Recover` links it |
| No `external_id`, and no matching run for 30 minutes | Yes | `Planner.Recover` completes it as neutral |
| No `external_id`, and several matching runs | No | Recovery leaves it pending; a person decides |

The deadlines are judged on the job's **status**, never on its `started_at`, which GitHub fills
in for a queued job too, and they hold whatever the job's `main-watcher-tests-finished` step
shows: the job has not completed, so no row of the outcome table applies yet. The run deadline
uses the `timeout` recorded in the check run's own output when the run was dispatched, not
`targets.yml` as the worker reads it this cycle, so the worker and the Planner agree about a
running job even while a target's `timeout` is being edited. A completed job is
never stale, with or without an artifact. The worker only flags these; `watch.yml` does the
stopping, and [watcher.md](watcher.md#stale-target-runs) describes the lifecycle. A stale run is
not a report owed, so it never feeds the "reporting pending" alert.

`MW_QUEUE_DEADLINE_MINUTES` shortens the 30-minute queue deadline to 1–30 minutes for scenario
tests (TS-S16 (g)); anything else falls back to 30. `watch.yml` reads the same variable, and the
two **must** be given the same value, or the worker starts cycles for runs the Planner does not
judge stale. Set it in the sandbox overlay's ConfigMap and in the sandbox watcher repo's
variables together, and remove both afterwards.

Each kind of work is also dated, which is what lets the hourly sweep say how long it waited for
the worker: a stale run by the deadline it passed, or by the time the stop was asked for. The
worker itself uses that time for one thing only, the "reporting pending" alert below.

Then the head of `main` is work when `Eligibility.CanStart` says it is: no check run and
the last test started more than `poll_interval` ago, or a newest `neutral` result older than
`poll_interval` while the head has fewer than 3 neutral results (ADR-017). The worker calls
the shared rule in `MainWatcher.Core` and keeps no copy; it never forces, so the
three-neutral cap holds until someone dispatches `watch.yml` with `force: true`. The rule's
fixtures, `tests/MainWatcher.Core.Tests/Fixtures/eligibility.json`, are read by the rule's
own tests, the Planner's and the worker's (TS-U3, TS-U5, TS-U13).

Last, an open lock whose lease wants renewing is work (ADR-014). The gate enforces a lock only
while its `lease_until` marker is in the future, so the worker asks for a cycle once a lease is an
hour old, or halfway through `lock_lease` where that comes sooner, and again for a lease that is
missing, unreadable or further ahead than a renewal could have set it. The half only bites below a
two-hour `lock_lease`, which in practice means the sandbox's ten minutes: a fixed hour would there
ask for the renewal only once the lease had expired, so every renewal would follow a window in
which the gate had stopped enforcing a lock nobody had abandoned. Only locks
authored by the App count, as they do for the gate and the Reporter; `MW_BOT_LOGIN` names that App
where it is not `main-watcher[bot]`. The work is dated by the moment renewal became due, and it
carries no check ID: it is not a report owed. `watch.yml` does the renewing, and
[watcher.md](watcher.md#lock-lease) describes the lease and what a lapse records.

An open lock that owes a **queue sweep** is work too, and is asked for before the lease the same
read found (ADR-016): until the sweep finishes, a merge group that passed its gate before the lock
existed can still merge onto a red `main`. A sweep is owed while the lock's `queue_swept` marker
is missing or older than its `sweep_required`, so a watcher that crashed part-way through one is
simply asked again. Only an **open** lock owes one: a closed lock enforces nothing, and a debt
nothing could discharge would ask for cycles for ever. The work is dated by the generation owed,
which is when the lock opened or its lapsed lease was renewed, and it names the lock rather than a
check run. `watch.yml` does the sweeping, and [watcher.md](watcher.md#queue-sweep) describes it.

The sweep debt is **read on every cycle**, whether or not it is the reason the cycle is dispatched
for. Only one reason can be the reason, and a report owed or an eligible head is found first; a
target whose reports keep failing would otherwise have its sweep hidden for as long as that lasted,
which is exactly when the groups queued before its lock are still free to merge (PR #56 review).
So the open-lock read happens for every target every cycle, and the worker's silence about a sweep
means a cycle looked and found it finished, rather than that nothing looked.

Last of all, a lock that has **closed without being reconciled** is work (ADR-015 point 6). Its
window still owes a report for every pull request that merged during it without `fixes-main`, and
once the issue is closed nothing else would ask for a cycle: no push tests it, no lease renews it.
So the worker reads the App's `main-broken` issues in any state, updated within
`reconcile_lookback` (30 days), and flags the first whose marker does not say
`reconciled=complete`. A closure older than that window is not revisited, which is R-21 accepted.
The work is dated by the closure, so the hourly sweep can say how long the reports have been owed,
and it carries no check ID. `watch.yml` does the reconciling, and
[watcher.md](watcher.md#reconciliation) describes it.

The worker therefore makes about five read calls per target per cycle while a target is
idle, plus one for the watcher repo's `watch.yml` runs (R-13). The open-lock read is one of them
and is made every cycle, since the lease and the sweep debt both come out of it; the sweep costs no
read of its own, being a marker in the same issue. The closed-lock read is made only when nothing
else has asked for a cycle, so a busy target does not pay for it. Both use the Issues: read
permission ADR-014 gave `mw-observer`. A cycle that finds an eligible head reads that target's
repository activity as well, to date the head's push; an idle target never pays for that.

## Configuration

All settings come from the environment. Anything missing or invalid is reported at once and
the worker exits with code 2 without starting a cycle.

| Variable | Meaning | Default |
| --- | --- | --- |
| `MW_WATCHER_REPO` | The watcher repo, `owner/repo`: where `targets.yml` and `watch.yml` live | Required |
| `MW_MAIN_WATCHER_APP_ID` | The `main-watcher` App's ID, which identifies its check runs | Required |
| `MW_OBSERVER_APP_ID`, `MW_OBSERVER_KEY_FILE` | `mw-observer`'s App ID and PEM private key file | Required |
| `MW_DOORBELL_APP_ID`, `MW_DOORBELL_KEY_FILE` | `mw-doorbell`'s App ID and PEM private key file | Required |
| `MW_CHECK_PERIOD_SECONDS` | Cycle interval, 10 to 3600 | 60 |
| `MW_TARGETS_PATH` | Path of the target list in the watcher repo | `targets.yml` |
| `MW_BOT_LOGIN` | The App that authors lock issues, whose leases the worker reads | `main-watcher[bot]` |
| `MW_GITHUB_API_URL` | GitHub REST base URL | `https://api.github.com/` |
| `ASPNETCORE_HTTP_PORTS` | The port `/healthz` listens on | 8080 in the image |

Before its first cycle, the worker mints one installation token per App on the watcher
repo. A rejected credential (HTTP 401, 403 or 404: the wrong key, the wrong App ID, or the
App not installed there) is a configuration error, so the worker exits with code 2 and the
pod enters `CrashLoopBackOff` rather than running healthily while doing nothing. Each check
has 30 seconds; a stalled or unreachable GitHub is transient and only logged, since the
cycles try again. The check runs inside the cycle loop, after `/healthz` is already serving,
so it cannot itself trip the liveness probe.

A `targets.yml` that cannot be parsed is a configuration error on the first cycle, so the
worker exits rather than watching nothing. Later, a broken file is logged and the previous
target list is kept, because `watch.yml` would reject it too. A read that fails is only
logged: the next cycle tries again.

Each App gets an installation token scoped to the one repository it is used for, minted with
an RS256 JWT and reused until five minutes before it expires. `mw-observer` therefore reads
each target with a token for that target only, and `mw-doorbell` holds one for the watcher
repo, where it may start workflows and open alert issues (ADR-010, §8 of ARCH-001).

## Health

`/healthz` answers 200 while a cycle has finished within `CycleTimeout` (5 min) plus two
check periods, counting from start-up, and 503 otherwise. A cycle is "finished" whatever it
found, including a cycle that failed on a GitHub error: a restart does not fix GitHub, but
it does fix a hung worker. A cycle running longer than `CycleTimeout` is cancelled, so one
stuck request cannot stop the loop. Its body also carries the last cycle's time and counts.

A cycle that keeps failing on GitHub therefore leaves the pod `Running`: the credential
check catches a credential that was wrong from the start, and a key revoked while the worker
is running is caught by the alerts below, not by the probe.

The endpoint exists for the liveness probe; nothing exposes it outside the cluster.

## Alerts

After each cycle the worker judges its own health and raises `watcher-infra` issues in the
watcher repo through `mw-doorbell`, which holds Issues: write there (ADR-012). The
conditions are:

| Condition | Alert title |
| --- | --- |
| Three cycles in a row failed: the cycle itself threw, or any target errored | The trigger worker's cycles keep failing |
| `watch.yml` runs are being started, and none has finished for 2 h | No `watch.yml` run has completed in 2 hours |
| GitHub answered 401, 403 or 404 to an installation-token request | A GitHub App credential is being refused |
| A response left less than 20% of a rate-limit budget (R-13) | GitHub rate limit below 20% |
| A report has been owed for more than 15 min (ADR-013 point 6) | Reporting pending on `owner/repo` |
| A queue sweep has been owed for more than 15 min (ADR-016 point 2) | Queue sweep unfinished on `owner/repo` |
| A `watch.yml` run for the target has not started by its deadline, and the worker is stopping it (ADR-020) | `watch.yml` run stuck `<state>` on `owner/repo` |
| Such a run is still not stopped 30 min after its deadline | `watch.yml` run could not be stopped on `owner/repo` |
| Such a run is waiting at a gate that lists reviewers, so it is left alone | `watch.yml` run waiting for a reviewer on `owner/repo` |

**De-duplication (TS-U7).** Each condition is raised once when it starts to hold, and at
most once an hour while it goes on holding. An open `watcher-infra` issue with the same
title gets a comment rather than a second issue, so a lasting fault is one thread. A
condition that clears is forgotten, so its next occurrence alerts at once; the issue stays
open for someone to read and close.

No alert is a required write. One that fails is logged, and the condition, which still
holds, is judged again on the next cycle: the alert channel is GitHub, which is often what
is failing. The whole review has its own 2-minute budget, so it cannot delay a cycle.

An idle watcher raises nothing: with no `watch.yml` run started, completing none is exactly
right, however long it lasts. The "no run completed" clock starts at start-up, so a restart
gives the watcher 2 h before the alert can fire. It is then set from the newest completed
run's own finishing time, not from the cycle that read it: the run list covers two hours, so
the same finished run is read again on every cycle until it ages out, and counting each of
those readings as a completion would let the alert take nearly four hours.

**Reporting pending.** A check run that is still `in_progress` while its target run's
`main-watcher` job has completed is a report the Reporter owes (ADR-013 point 3). The
worker already flags that as work; it now also times it, from the job's own `completed_at`,
so the clock survives a worker restart. A run that no longer exists has no job to date it,
so the first cycle that saw the report owed starts the clock instead. Such a check run is
pending, never stale: the deadlines of ADR-013 point 5 apply only to a job that has **not**
completed.

Every target still owing a report is judged on every cycle, whether or not that cycle looked
at it. A target whose own `watch.yml` run is queued or running is skipped by the cycle, so
judging only what the cycle saw would hold the alert back exactly when reporting is slowest:
a Reporter that is itself waiting for a runner. A target stops owing a report when a cycle
looks at it and finds nothing owed, or when it leaves `targets.yml`, since nothing will
report a target the watcher no longer watches. A cycle that threw names no targets, so it
forgets none.

**Queue sweep unfinished.** A lock owing a sweep is timed the same way, from the generation its
`sweep_required` marker names, and for the same reason: while it is owed, a group queued before
the lock can merge onto a red `main` and would be reported afterwards rather than blocked. The
usual cause is a gate run that is still going, since the sweep waits for it and re-runs it once it
completes, or one GitHub refuses to re-run; the `watch.yml` run log says which group and which
run. It is its own condition per target, judged from the sweep debt the cycle read rather than from
the reason it dispatched for, so it neither hides nor is hidden by a report pending on the same
target — which is the case that matters, since a report that keeps failing is what keeps a sweep
waiting. It clears when a cycle looks at the target and finds the sweep finished.

**Rate limit.** Every GitHub response the worker receives is read for
`x-ratelimit-remaining` and `x-ratelimit-limit`, across both Apps and every installation,
and the lowest budget of the cycle is the one judged. A response without those headers says
nothing, so it neither raises nor clears the condition.

## Container and deployment

```sh
docker build --file src/MainWatcher.Worker/Dockerfile --tag main-watcher-worker:dev .
src/MainWatcher.Worker/smoke-test.sh main-watcher-worker:dev
```

The smoke test, which also runs in CI (`worker-image` in `ci.yml`), checks three things: the
image fails fast with no configuration; `/healthz` answers 200 against a GitHub that accepts
connections and never replies, so a stalled API cannot delay the liveness endpoint; and a
credential GitHub rejects stops the container with code 2 instead of leaving it idle.

`deploy/worker/base` holds plain manifests: one replica, `Recreate` rollouts (more replicas
would need leader election), the liveness probe, requests of 50m CPU and 128Mi memory
`[assumption]`, a read-only root filesystem, a non-root user, and no Service or Ingress.
`deploy/worker/sandbox` is the sandbox overlay: namespace `main-watcher-sandbox`, the
sandbox App IDs, and a locally built image.

The two private keys are not in the manifests. Create the Secret they mount once, from the
keys downloaded when the Apps were registered:

```sh
kubectl -n main-watcher-sandbox create secret generic trigger-worker-keys \
  --from-file=observer.pem=<mw-observer key> --from-file=doorbell.pem=<mw-doorbell key>
```

```sh
kubectl apply -k deploy/worker/sandbox
kubectl -n main-watcher-sandbox rollout status deploy/trigger-worker
kubectl -n main-watcher-sandbox logs -f deploy/trigger-worker
```

Both keys are read once, at start-up, so a replaced or rotated key takes effect only on the
next restart:

```sh
kubectl -n main-watcher-sandbox rollout restart deploy/trigger-worker
```

To stop the worker for a scenario test that needs it down (TS-S7, TS-S11):

```sh
kubectl -n main-watcher-sandbox scale deploy/trigger-worker --replicas=0
```

Production uses the same base with the organisation's registry and secret store, and the
production watcher repo and App IDs (ARCH-001 §10). Rolling back means redeploying the
previous image tag.

## Validation

`dotnet test` covers the worker: `tests/MainWatcher.Worker.Tests` holds the work rules
(TS-U5 (a) and (b), TS-U3), the shared eligibility fixtures (TS-U13), the cycle's
dispatching, configuration validation, the liveness rule, and each alert condition with its
de-duplication (TS-U7). TS-S1 and TS-S2 were driven by the deployed worker in the sandbox
([issue-14-validation.md](../sandbox/issue-14-validation.md)), and TS-S14 (c) — the
"reporting pending" alert with the Reporter's issue writes failing for 20 minutes — in
[issue-15-validation.md](../sandbox/issue-15-validation.md).
