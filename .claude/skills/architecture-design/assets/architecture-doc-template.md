---
id: ARCH-001
type: architecture
status: draft              # draft | proposed | agreed | superseded | retired
state: target              # current | target | transition
owner:                     # a role or a person, never blank
reviewed: YYYY-MM-DD
review_by: YYYY-MM-DD
review_trigger:            # optional: the condition that should prompt a re-read
sources: []                # US-/FR-/NFR-/EP- IDs, docs, systems this was derived from
confidence: assumed        # confirmed | assumed | unverified
---

# [System name] — Architecture

> **Template notes — delete this block.** Sections marked **[standard]** belong in a
> standard-tier document, **[full]** in a comprehensive one; a lightweight document uses
> only the unmarked ones. Delete any section you have nothing real to put in — an empty
> heading implies the question was considered when it was not. Tag claims whose status is
> not obvious: `[assumption]`, `[recommendation]`, `[open]`, `[unconfirmed]`.

## Confirmation queue

Generated content not yet confirmed by a human. Empty this before treating the document as
agreed; anything left here is a guess that a reader might otherwise mistake for a decision.

| # | Item | Section | Why it matters | Who confirms |
|---|---|---|---|---|
| C-1 | | | | |

---

## 1. Overview

Two or three paragraphs a new engineer could read to know what this system is, who it
serves, and the shape of the solution. Name the single most consequential decision here and
link its ADR — a reader who stops after this section should still leave with the right
mental model.

## 2. Drivers

What actually shaped this design. Keep the IDs: they are the link back to the backlog and
they let a reviewer challenge a design choice against a real requirement.

| Driver | Type | Source | Consequence for the design |
|---|---|---|---|
| | Fact / Constraint / Assumption | NFR-004 | |

### Quality attributes, in priority order

Forcing the ranking is what makes later trade-offs decidable rather than a matter of taste.

| Rank | Attribute | Target and conditions | How it will be verified |
|---|---|---|---|
| 1 | | | |

### Constraints

Non-negotiables: existing systems, mandated platforms, regulation, budget, dates. Say who
imposes each and whether it is legal, contractual or technical.

### Assumptions

| # | Assumption | Impact if wrong | Owner | Confirm by |
|---|---|---|---|---|
| A-1 | | | | |

### Scope

In scope, and — more importantly — what is explicitly out, and where that work is tracked.

### Domains considered

Which of the fourteen concern areas were judged in play, and which were set aside. An
unexamined domain and a deliberately excluded one look identical unless this says which.

## 3. System context

One line naming the question the diagram answers, then the diagram, then a short paragraph
per external dependency: what it provides, its expected availability, and what happens here
when it is unavailable.

```mermaid
%% name: system-context
flowchart TB
```

## 4. Solution overview

```mermaid
%% name: container-view
flowchart TB
```

| Container | Responsibility | Technology | Owner | Serves |
|---|---|---|---|---|
| | | | | US-001 |

Every row should cite a requirement. A container that cannot is either unnecessary or
evidence of a missing story.

## 5. Key flows

For each scenario that crosses a boundary, handles money, or has a failure path worth
designing: the story it implements, the sequence, and the failure behaviour.

### 5.1 [Scenario] (US-0nn)

```mermaid
%% name: seq-scenario-name
sequenceDiagram
```

**Failure behaviour.** Timeouts, partial failure, duplicate delivery, and what the user sees.

## 6. Data **[standard]**

Domain concepts and invariants, ownership (which component is the system of record for
what), consistency requirements per operation, classification, retention and deletion, and
the migration path if this replaces something.

```mermaid
%% name: data-model
erDiagram
```

| Data | System of record | Derived copies | Classification | Retention | Residency |
|---|---|---|---|---|---|

## 7. Component detail **[standard]**

Internal structure of the containers complex enough to warrant it, and the reasoning behind
the internal boundaries.

## 8. Security **[standard]**

Trust boundaries, authentication per principal type, the authorisation model and matrix,
secrets and key custody, audit events. Link the threat model rather than duplicating it.

| Role | Resource | Create | Read | Update | Delete |
|---|---|---|---|---|---|

## 9. Integrations **[standard]**

| Dependency | Owner | Criticality | Interaction | SLA (stated/observed) | On failure |
|---|---|---|---|---|---|

## 10. Deployment and networking **[standard]**

Environments, topology, network controls and ports, TLS termination, trust boundaries,
secrets at runtime, release and rollback strategy, and scaling behaviour.

```mermaid
%% name: deployment-view
flowchart TB
```

## 11. Cross-cutting concerns **[standard]**

Only those with real decisions behind them: observability and SLOs, error handling and
resilience patterns, configuration, data protection, accessibility, internationalisation.

## 12. Quality attribute response **[full]**

The mechanism satisfying each significant NFR, and the test that would fail if it did not.
This turns "shall handle 500 concurrent users" from an aspiration into a commitment.

| Requirement | Mechanism | Verified by | Status |
|---|---|---|---|

## 13. Technology and versions **[standard]**

| Technology | Used for | Version | Support ends | Upgrade owner |
|---|---|---|---|---|

## 14. Ownership **[standard]**

| Component / asset | Owns | Operates | Tests | Approves change |
|---|---|---|---|---|

## 15. Decisions

| ADR | Decision | Status | Review trigger |
|---|---|---|---|
| ADR-001 | | Accepted | |

## 16. Risks and open questions

| # | Risk or question | Impact | Likelihood | Mitigation / default | Owner |
|---|---|---|---|---|---|
| R-1 | | | | | |
| Q-1 | | | | | |

A design document listing no risks is not trusted by experienced reviewers, and rightly so.

## 17. Evolution **[full]**

Current versus target state, transitional decisions and their triggers, expected change and
where the seams are, technical debt accepted deliberately, and the retirement plan.

## 18. Cost **[full]**

| Cost driver | Unit | At current volume | At 10× | Notes |
|---|---|---|---|---|

## 19. Glossary **[standard]**

Domain terms, used consistently here, in the backlog, and in the code.

## 20. Change log

| Date | Change | By | Affected decisions |
|---|---|---|---|

Updates are incremental. A reversed decision is a new ADR superseding the old one, never an
edit to the original.
