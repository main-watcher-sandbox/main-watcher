---
id: IDX-001
type: architecture
status: draft
state: current
owner: platform-team
reviewed: 2026-09-16
review_by: 2027-03-15
sources: [ARCH-001]
confidence: confirmed
---

# Architecture artifact index — Main Watcher

## Artifacts

| ID | Artifact | Type | State | Status | Owner | Reviewed | Review by / trigger |
|---|---|---|---|---|---|---|---|
| ARCH-001 | [Architecture](architecture.md) | architecture | target | proposed | platform-team | 2026-09-15 | 2027-03-15 |
| ADR-001 | [Central watcher](decisions/ADR-001-central-watcher.md) | adr | target | accepted (amended by ADR-009, ADR-010) | platform-team | 2026-09-15 | More than 20 targets |
| ADR-002 | [Merge-queue gate](decisions/ADR-002-merge-queue-gate.md) | adr | target | accepted (amended by ADR-008, ADR-014, ADR-016) | platform-team | 2026-09-15 | Native pause ships |
| ADR-003 | [State in GitHub](decisions/ADR-003-state-in-github.md) | adr | target | accepted (amended by ADR-013, ADR-017) | platform-team | 2026-09-15 | Walk-back above 50 calls |
| ADR-004 | [Resolution semantics](decisions/ADR-004-resolution-semantics.md) | adr | target | accepted | platform-team | 2026-09-15 | Override frequency |
| ADR-007 | [Test result contract: CTRF](decisions/ADR-007-ctrf-test-result-contract.md) | adr | target | accepted | platform-team | 2026-09-15 | A non-xUnit-v3 target appears |
| ADR-008 | [Gate fails open, with reconciliation](decisions/ADR-008-gate-fails-open-with-reconciliation.md) | adr | target | accepted (amended by ADR-014, ADR-015) | platform-team | 2026-09-15 | Unlabelled merges during a lock |
| ADR-009 | [Tests run in target repos](decisions/ADR-009-tests-run-in-target-repos.md) | adr | target | accepted | platform-team | 2026-09-15 | `actions: write` rejected |
| ADR-010 | [Self-hosted trigger worker](decisions/ADR-010-self-hosted-trigger-worker.md) | adr | target | accepted (amended by ADR-012, ADR-013, ADR-014, ADR-017) | platform-team | 2026-09-15 | Webhook hosting available |
| ADR-011 | [Test-duration metrics, phase 1](decisions/ADR-011-test-duration-metrics-phase-1.md) | adr | target | accepted | platform-team | 2026-09-15 | Cross-repo views needed |
| ADR-012 | [Worker alerts via GitHub issues](decisions/ADR-012-worker-alerts-via-github-issues.md) | adr | target | accepted | platform-team | 2026-09-15 | Monitoring stack adopted |
| ADR-013 | [Reporter completes the check run last](decisions/ADR-013-reporter-completes-check-run-last.md) | adr | target | accepted | platform-team | 2026-09-15 | "Reporting pending" alerts recur |
| ADR-014 | [Lock lease](decisions/ADR-014-lock-lease.md) | adr | target | accepted | platform-team | 2026-09-15 | Locks lapse more than once a quarter |
| ADR-015 | [Reconcile through closure, labels at merge time](decisions/ADR-015-reconcile-through-closure.md) | adr | target | accepted | platform-team | 2026-09-15 | Closed lock left unreconciled |
| ADR-016 | [Re-check queued groups when a lock opens](decisions/ADR-016-recheck-queued-groups-on-lock.md) | adr | target | accepted | platform-team | 2026-09-15 | TS-S17 disproves A-7 |
| ADR-017 | [Retry neutral results](decisions/ADR-017-retry-neutral-results.md) | adr | target | accepted | platform-team | 2026-09-15 | "Head untestable" alerts recur |
| TS-001 | [Test strategy](test-strategy.md) | test-strategy | target | draft | platform-team | 2026-09-16 | 2027-03-15 |

### Superseded (kept for history)

- [ADR-005 — JUnit XML contract](decisions/ADR-005-test-result-contract.md): replaced by the
  CTRF decision (ADR-007).
- [ADR-006 — App identity with split tokens](decisions/ADR-006-app-identity.md): replaced by
  running tests in target repos (ADR-009).

## Deliberately not produced

| Artifact | Why not | Would become necessary if |
|---|---|---|
| Separate threat model | Three Apps and one outbound-only worker; covered in ARCH-001 §8 | Webhook receiver (public endpoint), or cross-organisation targets |
| Metrics store design (schema, retention jobs) | Deferred with phase 2 (ADR-011) | Phase 2 is approved |
| Capacity plan | Fewer than 20 targets; rate limits tracked as R-13 | More than 20 targets |
| Data model / ERD | No datastore (ADR-003) | A database is introduced |
| Migration plan | New system | — |
| Requirements traceability matrix file | No backlog; traced inline in ARCH-001 §4 and TS-001 §8 | A backlog is created |

## Outstanding confirmations

None. All confirmation-queue items are settled: CQ-1 to CQ-9 during design, and CQ-10 to
CQ-14, which came from adversarial reviews, on 2026-09-15, all as proposed.

**A-7**, the GitHub re-run behaviour ADR-016 relies on, was confirmed by a sandbox spike on
2026-09-16 (MainWatcher#6). TS-S17 still tests the whole queue sweep before rollout.

Remaining `[assumption]` tags in ARCH-001: worker resource sizing, the .NET version,
and the sandbox organisation name.
