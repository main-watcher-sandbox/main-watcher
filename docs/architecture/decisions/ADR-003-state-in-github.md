---
id: ADR-003
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "reporter walk-back exceeds ~50 API calls, or GitHub check retention becomes a problem"
sources: [FR-3, ARCH-001]
confidence: confirmed
---

# ADR-003 — No datastore: check runs and the lock issue hold all state

**Deciders:** platform team · **Consulted:** —

## Context

The watcher needs three pieces of state:

- **Which commit was last tested per target**, so it can skip unchanged heads (FR-2).
- **Which commit was last green**, so it can list pushes since then (FR-3).
- **Whether the target is locked** (FR-4).

A separate store would need hosting, backup, and a way to keep it in step with GitHub.

## Decision

We will keep all state in GitHub:

- **Check runs.** The App posts a `main-watcher/integration` check run on every tested
  commit in the target. Its presence means "tested"; its conclusion means green, red or
  infrastructure error.
- **Last green.** The Reporter walks `main` history back from the failing commit until it
  finds a successful Main Watcher check run. While a lock is open, it reads the value from
  the issue's hidden marker instead.
- **Lock.** An open issue labelled `main-broken` and authored by the App.

We chose this because GitHub is then the only source of truth, the state is visible to
target teams, and there is nothing to operate.

## Options considered

### Option A — State in GitHub objects *(chosen)*

### Option B — A state file on a branch of the watcher repo

- **Where it is better:** cheap reads.
- **Why it lost:** concurrent writes conflict, it can drift from reality, and target teams
  can't see it.

### Option C — External database

Overkill for fewer than 20 targets; it adds hosting and secrets.

## Consequences

**Positive**
- Results are visible on the target's commits.
- Nothing to back up.
- Onboarding needs no schema.

**Negative**
- **Walk-back cost.** Finding "last green" takes one API call per commit walked. This is
  small with frequent polling, but grows after long outages; it is capped at 100 commits.
- **Force-pushes can remove the last green commit** from history. The Reporter then falls
  back to timestamps.
- **Deleted check runs or issues** silently change state.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Planner skip logic, Reporter walk-back | TS-S1, TS-S3 | Walk-back length logged in the job summary |

## Revisit when

The walk-back regularly exceeds ~50 calls, or the watcher needs history longer than GitHub
keeps.
