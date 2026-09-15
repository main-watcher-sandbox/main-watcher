---
id: ADR-012
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "the organisation adopts a monitoring/alerting stack"
sources: [A-6, ADR-010]
confidence: confirmed
amends: ADR-010
---

# ADR-012 — The trigger worker alerts through GitHub issues (amends ADR-010)

**Deciders:** requester, platform team · **Consulted:** —

## Context

ADR-010 assumed the cluster had an alerting stack (A-6) and sent the worker's health
alerts there. The requester confirmed on 2026-09-15 that **no monitoring or alerting stack
exists**.

## Decision

**Alerts go through GitHub.** The worker raises its alerts as `watcher-infra` issues in
the watcher repo, the same channel every other Main Watcher alert uses. Alerts are
de-duplicated: an existing open issue with the same title gets a comment instead of a new
issue. The alerts are:
- no completed `watch.yml` run in 2 h;
- repeated cycle errors (3 in a row);
- token or permission failures.

**Permission change.** `mw-doorbell`, which is installed on the watcher repo only, gains
**Issues: write**. Its permissions become Actions: write and Issues: write, on the watcher
repo only.

**Worker failure.**
- The Kubernetes liveness probe on `/healthz` restarts a hung worker.
- A worker that is down entirely is still detected by the hourly sweep's "trigger worker
  appears down" alert (ADR-010).
- The `/metrics` endpoint is dropped until a metrics stack exists.

## Options considered

### Option A — Alerts to a cluster alerting stack (ADR-010 as written)

Disqualified: no such stack exists.

### Option B — Alerts as GitHub issues *(chosen)*

### Option C — Email or chat webhooks from the worker

Rejected because it would add another credential and another channel. Teams can already
subscribe to `watcher-infra` issues in GitHub.

## Consequences

**Positive**
- One alert channel for everything.
- No new infrastructure.

**Negative**
- **Notifications depend on GitHub.** Someone must watch the watcher repo or subscribe to
  the label.
- **Nothing alerts during a full GitHub outage.** This is accepted, because nothing can be
  tested then either.
- **A leaked doorbell key could also create spam issues** in the watcher repo (R-12).

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Worker `AlertSink` (GitHub issues) | TS-S11, TS-U7 | Platform team subscribed to `watcher-infra` |

## Revisit when

The organisation adopts a monitoring and alerting stack, or needs paging outside GitHub.
