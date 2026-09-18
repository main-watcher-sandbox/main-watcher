---
owner: platform-team
reviewed: 2026-09-18
review_by: 2027-03-15
---

# Trigger worker

`src/MainWatcher.Worker` is the self-hosted .NET service that starts `watch.yml` when a
target has work (ADR-010). It is a `BackgroundService` beside a single HTTP endpoint,
`/healthz`, for the Kubernetes liveness probe. It makes outbound HTTPS calls only, and
needs no inbound Service or Ingress.

Issues #14 and #15 cover the cycle and the alerts described here, and #16 the hourly backup
sweep, which does this work when the worker is not and says so; see
[watcher.md](watcher.md#backup-sweep). #18 adds the stale-run deadlines below. Lease renewal
(#19), merge reconciliation (#20) and the queue sweep (#21) are separate backlog items; a target
whose only work is one of those is not flagged yet.

## A cycle

Every `check_period` (default 60 s), the worker:

1. reads `targets.yml` from the watcher repo's `main` through `mw-observer`;
2. reads `watch.yml` runs that are still queued or running, and skips those targets: such a
   run acts on the current state, so another dispatch would only queue a cycle with nothing
   left to do. `watch.yml`'s `run-name` is `watch <owner/repo>`, which is how the worker
   tells whose cycle it is. When that read fails, no target is skipped, because a duplicate
   dispatch is harmless (ADR-010);
3. for each enabled target, looks for work (below) through that target's `mw-observer`
   gateway, reusing one `GitHubGateway` per target so its check-run snapshot is kept;
4. starts `watch.yml` once for each target with work, through `mw-doorbell`, passing
   `target`. The dispatch POST is never retried: a lost response may still have started the
   run, and the next cycle looks again.

A target whose reads fail is logged and counted; the other targets are still processed. All
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
| A started job past its `started_at` plus the target's `timeout`, the workflow's 20-minute margin and a 10-minute grace | Yes | The same |
| A run already asked to stop, until its job has completed | Yes | The Planner asks again, force-cancels and finally alerts |
| No `external_id`, and exactly one matching target run | Yes | `Planner.Recover` links it |
| No `external_id`, and no matching run for 30 minutes | Yes | `Planner.Recover` completes it as neutral |
| No `external_id`, and several matching runs | No | Recovery leaves it pending; a person decides |

The deadlines are judged on the job's **status**, never on its `started_at`, which GitHub fills
in for a queued job too, and they hold whatever the job's `main-watcher-tests-finished` step
shows: the job has not completed, so no row of the outcome table applies yet. A completed job is
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

Otherwise the head of `main` is work when `Eligibility.CanStart` says it is: no check run and
the last test started more than `poll_interval` ago, or a newest `neutral` result older than
`poll_interval` while the head has fewer than 3 neutral results (ADR-017). The worker calls
the shared rule in `MainWatcher.Core` and keeps no copy; it never forces, so the
three-neutral cap holds until someone dispatches `watch.yml` with `force: true`. The rule's
fixtures, `tests/MainWatcher.Core.Tests/Fixtures/eligibility.json`, are read by the rule's
own tests, the Planner's and the worker's (TS-U3, TS-U5, TS-U13).

The worker therefore makes about three read calls per target per cycle while a target is
idle, plus one for the watcher repo's `watch.yml` runs (R-13). A cycle that finds an eligible
head reads that target's repository activity as well, to date the head's push; an idle target
never pays for that read.

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
