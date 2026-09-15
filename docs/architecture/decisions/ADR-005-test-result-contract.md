---
id: ADR-005
type: adr
status: superseded
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "a target cannot produce JUnit XML"
sources: [FR-3, A-3]
confidence: confirmed
---

# ADR-005 — Test script contract: exit code plus JUnit XML

> **Superseded by [ADR-007](ADR-007-ctrf-test-result-contract.md) on 2026-09-15.** All target
> repos are on xUnit v3, which writes CTRF natively. This record is kept unchanged for
> history.

**Deciders:** platform team · **Consulted:** target repo owners

## Context

The issue must list the failing tests (FR-3). Scripts differ between repos, so the watcher
needs one contract that works across languages.

## Decision

Each target's script:
- exits non-zero when tests fail;
- writes one or more JUnit XML files to a path set in `targets.yml`.

The Runner retries failed tests once, through an optional `retry_command` that receives the
failing test IDs, or by rerunning the whole script when none is given.

What the watcher concludes from each outcome:

| Script outcome | Watcher result |
|---|---|
| Exit 0 | Green, even if the XML shows failures; a warning is logged |
| Non-zero exit with XML | Red; the failing tests are listed |
| Non-zero exit without XML | Red; the issue says "failing tests unknown" |
| Timeout or setup error | Infrastructure error |

## Options considered

### Option A — Exit code plus JUnit XML *(chosen)*

### Option B — Parse the script's console output

Needs nothing from the target. It lost because it is brittle and different for every
framework.

### Option C — A custom JSON results format

Richer and fully under our control. It lost because every framework already emits JUnit
XML, and nothing emits our JSON.

## Consequences

**Positive**
- Nearly every test framework supports it.
- One parser.

**Negative**
- **JUnit dialects vary**, so the parser must be lenient.
- **A retry can hide real flakiness.** The number of retries is recorded in the check run
  summary.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Runner, JUnit parser | TS-U1 (dialect fixtures) | Onboarding dry run |

## Revisit when

A target's framework cannot produce JUnit XML, or the team wants richer data such as
timings or flake history.
