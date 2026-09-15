---
id: ADR-010
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "the organisation gains a way to expose webhook endpoints, or the worker's API usage nears rate limits"
sources: [FR-2, C-6, C-7, ADR-001, ADR-009]
confidence: confirmed
amends: ADR-001
---

# ADR-010 — A self-hosted .NET worker triggers the watcher; GitHub's schedule is only a backup sweep

> **Amended by [ADR-012](ADR-012-worker-alerts-via-github-issues.md) on 2026-09-15.** No
> alerting stack exists, so the worker raises its alerts as `watcher-infra` GitHub issues, and
> `mw-doorbell` gains Issues: write on the watcher repo. The `/metrics` endpoint is dropped.

> **Proposed amendments, 2026-09-15, awaiting confirmation:**
> - [ADR-013](ADR-013-reporter-completes-check-run-last.md): an in-progress check run whose
>   target run has completed is "reporting pending", with or without an artifact. It is
>   never marked stale, and raises an alert after 15 minutes. Only a target run that has
>   not completed within its timeout + 10 min, or no longer exists, is stale.
> - [ADR-014](ADR-014-lock-lease.md): `mw-observer` gains Issues: read on targets. The
>   worker also flags work when a lock lease is due for renewal, or when a closed lock is
>   not yet reconciled (ADR-015).

**Deciders:** requester, platform team · **Consulted:** —

## Context

**GitHub's schedule is unreliable** (C-7). The requester has found scheduled workflows
unreliable even at 15-minute intervals. GitHub's troubleshooting docs confirm that
scheduled events can be delayed under high load, especially at the start of each hour,
and that some can be dropped. ADR-001 relied on that scheduler for timing.

**Hosting constraints** (C-6). The organisation is a .NET shop, has no Azure subscription,
and can host containers on Docker/Kubernetes. It was not established that inbound public
HTTPS endpoints are available.

## Decision

We will run a **trigger worker**: a .NET `BackgroundService` in a container, deployed to
the organisation's Kubernetes cluster as a single-replica Deployment. It makes outbound
calls only. Every `check_period` (default 60 s) it:

1. Reads `targets.yml` from the watcher repo.
2. For each enabled target, reads the `main` head and the newest Main Watcher check runs.
   It flags **work** when:
   - the head has no check run and the last test started more than `poll_interval` ago; or
   - an in-progress check run's target workflow run (`external_id`) has completed; or
   - an in-progress check run is older than the stale threshold.
3. If any work exists, starts the watcher workflow `watch.yml` via `workflow_dispatch`,
   passing the targets with work.
4. Exposes `/healthz` and Prometheus metrics: last cycle, last dispatch, errors.
5. Raises an alert when the watcher repo has no completed `watch.yml` run in the last
   2 hours. The alert goes to the cluster's existing alerting (assumed); see §11.

**Credentials: two single-purpose GitHub Apps.** GitHub gives an App the same permissions
on every repo it is installed on, so the two needs are split:

| App | Installed on | Permissions | Key stored in |
|---|---|---|---|
| `mw-observer` | Target repos + watcher repo | Metadata, Contents, Checks and Actions — all **read** | Kubernetes Secret |
| `mw-doorbell` | Watcher repo only | Actions: write | Kubernetes Secret |

The main `main-watcher` App key never leaves GitHub.

**Backup and cross-checks:**
- `watch.yml` keeps an **hourly schedule at minute 17** as a backup sweep. If the worker is
  down, work is still done within hours, not never.
- When the sweep finds work that has waited more than 15 minutes, it raises a
  `watcher-infra` alert saying **"trigger worker appears down"**.
- The worker and the sweep therefore watch each other.

**Duplicates are harmless.**
- `watch.yml` has a concurrency group that keeps at most one pending run.
- Every Planner step is idempotent: a head with a check run is never started twice.

## Options considered

### Option A — Harden the GitHub schedule only (odd minutes, catch-up logic)

No new infrastructure. It lost because it does not prevent dropped events, and every empty
tick costs a runner job.

### Option B — Self-hosted timer worker that checks for changes itself *(chosen)*

### Option C — Self-hosted webhook receiver for the App's `push` and `workflow_run` events

Reacts within seconds. It lost because it needs an endpoint reachable from the internet,
with TLS and firewall rules, which the organisation has not confirmed it can provide.

### Option D — External managed timer (e.g. Azure Functions)

Disqualified: there is no Azure subscription (C-6).

## Consequences

**Positive**
- **Detection within about 1–2 minutes** of a push or test completion, independent of
  GitHub's scheduler.
- **No inbound network exposure.**
- **Almost no wasted runs.** GitHub Actions runs only when there is work, which removes
  R-9.
- **The organisation's own stack:** .NET, containers, and its existing alerting.

**Negative**
- **A service to operate.** Image, deployment, upgrades, and two more App keys to rotate.
- **Extra API traffic.** About 2–3 read calls per target per cycle. That is well within
  installation rate limits for fewer than 20 targets, but it grows linearly (R-13).
- **Reduced reaction during worker outages.** While the worker is down, reaction time falls
  back to the hourly sweep, which is itself best-effort.
- **Split alerting.** Alerts are raised in two places: the cluster's alerting for the
  worker, and `watcher-infra` issues for the watcher.

**Follow-on work**
- Worker project, container image and Helm chart or manifests.
- Registration of the two Apps.
- Alert rules.
- The `check_period` setting.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Trigger worker, `watch.yml` sweep | TS-U5, TS-S11 | `/healthz`, missing-watcher-run alert, "worker appears down" alert |

## Revisit when

- The organisation can host public webhook endpoints (Option C).
- Worker API usage exceeds about 50% of the rate limit.
- More than one replica is needed, which would require leader election.
