---
id: ADR-004
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "overrides used more than twice a month on one repo"
sources: [FR-4]
confidence: confirmed
---

# ADR-004 — A green run closes the lock automatically; a human close is an override

**Deciders:** requester, platform team · **Consulted:** —

## Context

FR-4 says the queue stays paused "until the logged issue has been resolved". What counts
as resolved matters in two ways:

- **Too strict:** a false positive, such as a flaky test or a broken environment, blocks
  every team.
- **Too loose:** the lock means nothing.

## Decision

- **A passing run on a newer commit** makes the App close the lock issue with a comment
  naming the green commit.
- **A human may close the issue at any time.** That is an **override**:
  - the gate lifts immediately;
  - the App comments with who closed it and that `main` is still red;
  - the target is not re-locked until a failing run on a **newer** commit.
- **The App never reopens** a closed issue. A new failure opens a new issue.

## Options considered

### Option A — Resolved only when a human closes the issue

Simple and explicit. It lost because the lock outlives the fix whenever people forget to
close it.

### Option B — Resolved only by a green run

Strictest. It lost because it leaves no escape from a false positive other than merging a
"fix" for a problem that doesn't exist.

### Option C — Both, with the human close recorded as an override *(chosen)*

## Consequences

**Positive**
- Normal recovery needs no manual step.
- False positives can be escaped in seconds.

**Negative**
- **Overrides can hide real breakage.** A red `main` can merge more changes until the next
  push fails.
- **Two issues can exist per incident** if a failure recurs after an override. Each links
  to the previous one.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Reporter close/override logic | TS-S3, TS-S6 | Monthly override count per repo |

## Revisit when

Overrides happen more than twice a month on one repo, which suggests flakiness to fix at
the source, or a team asks for stricter locking.
