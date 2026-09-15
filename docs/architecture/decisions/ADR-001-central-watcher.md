---
id: ADR-001
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "more than 20 targets, or polling delay becomes a complaint"
sources: [FR-1, FR-2, ARCH-001]
confidence: confirmed
---

# ADR-001 — A central watcher repo polls targets and tests only the newest `main` commit

> **Amended on 2026-09-15** by [ADR-009](ADR-009-tests-run-in-target-repos.md) (tests now run in
> each target repo) and [ADR-010](ADR-010-self-hosted-trigger-worker.md) (a self-hosted worker
> triggers the watcher; the GitHub schedule is only an hourly backup). The central watcher
> and "test only the newest commit" still stand.

**Deciders:** requester, platform team · **Consulted:** —

## Context

The component must be reusable and able to target any repository (FR-1). The requester
explicitly does not want a run for every push. When several pushes have landed since the
last run, only the newest commit should be tested (FR-2). The failure report must still
list every push since the last green run (FR-3).

This choice decides:
- where test code runs;
- which secrets tests can reach;
- which identity the component uses.

Reversing it later would mean rebuilding the runner and the identity model.

## Decision

We will run a central **watcher repo**. Its scheduled Planner lists the targets from
`targets.yml`, skips any target whose `main` head already has a Main Watcher check run, and
starts one Test Runner per changed target. Each target has its own concurrency group, so a
newer head supersedes a pending older one.

We chose this because it matches the requester's model: point the watcher at a repo, and
test the latest commit on a schedule.

## Options considered

### Option A — Reusable workflow called from each target repo

Each target triggers its own run on push, with concurrency limiting it to the latest push.
This option was **fair on FR-2**: concurrency groups also give "latest only".

- **Where it is better:** it has direct access to the target's secrets and needs no App.
- **Why it lost:**
  - it runs on push, not on a schedule;
  - each target owns part of the logic;
  - it does not match the "pointed at a repo" model the requester wanted.

### Option B — Central watcher repo *(chosen)*

### Option C — Standalone GitHub App service driven by webhooks

- **Where it is better:** detection is immediate and it scales best.
- **Why it lost:** it means operating a service, and nothing requires that yet.
- **Status:** kept as the evolution path.

## Consequences

**Positive**
- One place to configure, observe and change behaviour.
- Test cost scales with how often `main` changes, not with the number of pushes.
- Adding a target is mostly configuration.

**Negative**
- **Detection delay** of up to one polling interval, and GitHub cron can start late (R-5).
- **Target secrets must be duplicated** into watcher-repo environments, which means two
  places to rotate them.
- **Target code runs inside the watcher repo.** This needs the token separation in
  ADR-006.
- **A GitHub App is now required** for cross-repo access.
- **Runner minutes** are billed to the watcher's organisation.

**Follow-on work**
- A heartbeat workflow.
- Onboarding documentation.
- The `targets.yml` schema, with validation in CI.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Planner job, per-target concurrency | TS-S1, TS-S2 | Missing-tick alert |

## Revisit when

- there are more than 20 targets;
- a team needs detection faster than about 15 minutes;
- targets span several organisations.
