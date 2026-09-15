---
id: ADR-008
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "reconciliation finds more than one unlabelled merge during a lock in a quarter"
sources: [FR-4, NFR-3, ADR-002, ADR-003]
confidence: confirmed
amends: ADR-002
---

# ADR-008 — The gate fails open on GitHub API errors, and the watcher reconciles afterwards (amends ADR-002)

> **Note (2026-09-15):** after ADR-010, "each tick" below means "each watcher run". The
> trigger worker starts a watcher run whenever `main` moves, and every merge moves `main`, so
> reconciliation still sees every merge.

**Deciders:** requester, platform team · **Consulted:** —

## Context

ADR-002 said the gate retries and then **fails closed** when it cannot reach the GitHub
API. The requester decided on 2026-09-15 that merge availability matters more: the gate
should **fail open**, but such events must be made visible.

A gate that fails open because the GitHub API is unreachable usually can't report the
problem through that same API. So visibility cannot depend only on the gate reporting it
at the moment it happens.

## Decision

**1. Gate behaviour.** When it still cannot read the lock state after 3 retries with
backoff, the gate:
- passes the merge group;
- emits a `::warning` annotation and a job summary headed **"LOCK STATUS UNKNOWN —
  failed open"**;
- makes a best-effort attempt, which may itself fail, to post a non-required
  `main-watcher/gate-fail-open` check run on the merge-group commit.

**2. Reconciliation by the watcher**, which does not depend on the gate having reported
anything. On each tick, for every target with an open lock issue, the Planner:
- reads the repository activity of type `merge_queue_merge` and `pr_merge` on `main` since
  the lock issue was created;
- checks each merged PR for the `fixes-main` label.

Any merged PR without the label is:
- appended to the lock issue under **"Merged while locked"**;
- raised as a `watcher-infra` alert to the platform team.

The last activity checked is kept in the lock issue's hidden marker, so each merge is
reported only once (ADR-003).

**3. Gate-level visibility.** The hourly heartbeat workflow also lists
`gate-fail-open` check runs posted in the past hour and adds them to the `watcher-infra`
alert. This is only a secondary signal, because those runs may never have been posted.

Everything else in ADR-002 stands.

## Options considered

### Option A — Fail closed (the original ADR-002 behaviour)

A lock is always enforced. It lost because a partial GitHub outage would block merges in
every onboarded repo.

### Option B — Fail open, with only the gate's own annotation

Simplest. It lost because nobody reads annotations on successful checks, and the
annotation may be the only trace of the event.

### Option C — Fail open, plus reconciliation by the watcher *(chosen)*

It detects the outcome that matters, an unlabelled merge during a lock, whether or not the
gate could report.

## Consequences

**Positive**
- A GitHub API outage never stops merges.
- Every harmful fail-open is reported, even if the gate couldn't report it.
- No new App permissions are needed: PR labels are read through the Issues API.

**Negative**
- **During an outage, a non-fix PR can land on a red `main`,** making the breakage worse.
  It is reported afterwards, not prevented.
- **Fail-opens while no lock is open go unreported** unless the best-effort check run was
  posted. This is accepted, since those events allowed nothing that wasn't allowed anyway.
- **Each tick makes extra activity API calls** for repos that are currently locked.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Gate template v1 fail-open path; Planner reconciliation | TS-S9, TS-U4 | `watcher-infra` alert "Merged while locked" |

## Revisit when

Reconciliation finds more than one unlabelled merge during a lock in a quarter, which
would mean fail-open is being hit too often to accept.
