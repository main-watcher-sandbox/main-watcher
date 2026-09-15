---
id: ADR-002
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "GitHub ships a native merge-queue pause, or the sandbox test of R-3 fails"
sources: [FR-4, C-1, C-2, C-3, C-4]
confidence: confirmed
---

# ADR-002 — Pause the merge queue with a gate workflow in each target repo

> **Amended by [ADR-008](ADR-008-gate-fails-open-with-reconciliation.md) on 2026-09-15.**
> On GitHub API errors the gate now fails **open**, and the watcher reconciles afterwards.
> The rest of this decision stands.

**Deciders:** requester, platform team · **Consulted:** security (App permissions)

## Context

While a failure is unresolved, the GitHub merge queue must be paused (FR-4). Three facts
constrain how:

- **GitHub's queue has no pause function or API** (C-1).
- **A fix must still be able to merge** while `main` is locked (C-2). Otherwise the lock
  can never be cleared.
- **Community reports say "Restrict updates" bypass lists don't work** (C-3). One report
  says even repository admins on the bypass list cannot merge. Another, from 2026, says the
  bypass option disappears when a ruleset requires a merge queue.

Because the watcher is central (ADR-001), it does not receive the target repos'
merge-queue events.

## Decision

We will add a **gate workflow** to each target repo and make it a required check in that
repo's merge-queue ruleset. It behaves as follows:

- **On `merge_group` events**, it fails when an open `main-broken` issue authored by
  `main-watcher[bot]` exists, unless **every** PR in the group has the `fixes-main` label.
- **On `pull_request` events**, it always passes.
- **On GitHub API errors**, it retries and then fails closed.

We chose this because it has no delay when `main` is healthy, needs no administration
permission, and has a fix path that does not depend on GitHub's bypass behaviour.

## Options considered

### Option A — Gate workflow in each target *(chosen)*

### Option B — The watcher polls the queue and posts the gate check

- **Where it is better:** no files in target repos.
- **Why it lost:** every merge, including on a healthy `main`, waits up to one polling
  interval.

### Option C — Switch a "freeze" ruleset on and off through the API

- **Where it is better:** a true hold. Queued PRs are not removed, and there is no file per
  repo.
- **Why it lost:**
  - it needs administration write permission;
  - if the watcher crashes, `main` stays frozen;
  - the reported bypass bugs (C-3) would block the fix.

### Option D — Mergify pause API

Disqualified: the only queue in use is GitHub's own (C-4).

## Consequences

**Positive**
- No merge latency while `main` is healthy.
- The App needs only checks and issues permissions.
- Fail-open when the watcher is down.
- Fixes merge through the normal queue, with its normal checks.

**Negative**
- **This is not a true pause.** Queued PRs that aren't fixes are removed and must be
  re-queued (R-7).
- **One more file per target repo**, which drifts unless its template version is pinned
  and tracked.
- **Batched merge groups** need every PR in the group checked. This depends on the
  `base_sha`/`head_sha` payload and on a compare call (R-3, A-5).
- **Anyone who can apply labels** can bypass the lock (R-4).
- **Fail-closed on API errors** can block merges during a partial GitHub outage.

**Follow-on work**
- A versioned gate template.
- A sandbox test (TS-S5) for batching and for label bypass.
- An unlock comment that lists the PRs the gate removed.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| `gate.yml` template v1, ruleset required check | TS-S4, TS-S5, TS-S6 | Onboarding checklist item 5 |

## Revisit when

- GitHub releases a native merge-queue pause;
- R-7 becomes a real complaint, in which case add automatic re-queue;
- the sandbox shows the bypass bugs are fixed, making Option C viable.
