---
owner: platform-team
reviewed: 2026-09-17
review_by: 2027-03-15
---

# Trigger worker

`src/MainWatcher.Worker` is the self-hosted .NET service that starts `watch.yml` when a
target has work (ADR-010). It is a `BackgroundService` beside a single HTTP endpoint,
`/healthz`, for the Kubernetes liveness probe. It makes outbound HTTPS calls only, and
needs no inbound Service or Ingress.

Issue #14 covers the cycle described here. The health alerts (#15), the hourly backup
sweep with "worker appears down" (#16), stale-run cancellation (#18), lease renewal (#19),
merge reconciliation (#20) and the queue sweep (#21) are separate backlog items; a target
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
| A target run whose `main-watcher` job is still queued or running | No | Nothing to report yet |
| No `external_id`, and exactly one matching target run | Yes | `Planner.Recover` links it |
| No `external_id`, and no matching run for 30 minutes | Yes | `Planner.Recover` completes it as neutral |
| No `external_id`, and several matching runs | No | Recovery leaves it pending; a person decides |

Otherwise the head of `main` is work when `Eligibility.CanStart` says it is: no check run and
the last test started more than `poll_interval` ago, or a newest `neutral` result older than
`poll_interval` while the head has fewer than 3 neutral results (ADR-017). The worker calls
the shared rule in `MainWatcher.Core` and keeps no copy; it never forces, so the
three-neutral cap holds until someone dispatches `watch.yml` with `force: true`. The rule's
fixtures, `tests/MainWatcher.Core.Tests/Fixtures/eligibility.json`, are read by the rule's
own tests, the Planner's and the worker's (TS-U3, TS-U5, TS-U13).

The worker therefore makes about three read calls per target per cycle while a target is
idle, plus one for the watcher repo's `watch.yml` runs (R-13).

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
check catches a credential that was wrong from the start, but a key revoked while the worker
is running only shows in the logs until the worker's own alerts (no `watch.yml` run in 2 h,
reporting pending, repeated errors, token failures) arrive with #15.

The endpoint exists for the liveness probe; nothing exposes it outside the cluster.

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
dispatching, configuration validation and the health rule. TS-S1 and TS-S2 were driven by
the deployed worker in the sandbox; the record is
[issue-14-validation.md](../sandbox/issue-14-validation.md).
