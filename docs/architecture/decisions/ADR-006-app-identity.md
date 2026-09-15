---
id: ADR-006
type: adr
status: superseded
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "targets span organisations, or self-hosted runners are introduced"
sources: [FR-1, ADR-001, ADR-002]
confidence: confirmed
---

# ADR-006 — GitHub App identity, with read-only tokens for jobs that run target code

> **Superseded by [ADR-009](ADR-009-tests-run-in-target-repos.md) on 2026-09-15.** Target code
> no longer runs in the watcher repo, so the split-token design is unnecessary. The GitHub App
> identity is carried into ADR-009. This record is kept unchanged for history.

**Deciders:** platform team · **Consulted:** security

## Context

The central watcher (ADR-001) must read target repos and write check runs and issues to
them. A workflow's built-in token cannot reach other repos. The Test Runner executes code
that anyone with write access to a target can change.

## Decision

- **Identity.** A GitHub App `main-watcher`, installed on the target repos, with these
  permissions:
  - Metadata: read
  - Contents: read
  - Checks: write
  - Issues: write
  - No administration permission.
- **Token separation:**
  - A token-minting step issues the Test Runner a token limited to `contents: read` on one
    repo.
  - Only the Planner and Reporter, which never execute target code, can reach the App
    private key. It is stored in the `reporter` environment.
  - Target secrets are in one environment per target, available only to that target's
    Runner.

## Options considered

### Option A — Personal access token or machine user

Rejected: it is tied to a person or seat, has broad scope, and its tokens are long-lived.

### Option B — GitHub App with a single token for all jobs

Rejected: target code could write issues and checks in every repo.

### Option C — GitHub App with split tokens *(chosen)*

## Consequences

**Positive**
- Tokens are short-lived and scoped to a single repo.
- The bot identity is clear, and the gate relies on it to recognise lock issues.

**Negative**
- **Someone must own and rotate the private key** (CQ-8).
- **Workflows are more complex:** extra jobs and environments.
- **A malicious target script can still read that target's own secrets.** This is
  accepted, because they belong to that target.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Workflow job and environment layout | TS-S8, workflow review | Environment protection rules on `reporter` |

## Revisit when

Targets span more than one organisation, self-hosted runners are introduced, or the App
needs new permissions (for example, automatic re-queue).
