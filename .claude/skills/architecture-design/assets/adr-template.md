---
id: ADR-0nn
type: adr
status: proposed          # proposed | accepted | superseded | deprecated
state: target             # current | target | transition
owner:
reviewed: YYYY-MM-DD
review_by: YYYY-MM-DD
review_trigger:           # the observable condition that should reopen this
sources: [US-0nn, NFR-0nn]
confidence: confirmed
supersedes:               # ADR-0nn, if this replaces an earlier decision
---

# ADR-0nn — [Short decision, stated as an outcome]

**Deciders:** who decided · **Consulted:** security, platform, whoever had to be asked

## Context

The forces in play, written so someone who was not in the room can reconstruct why this was
a genuine question. Include the constraints and requirement IDs, and anything about team or
timeline that mattered.

State what makes this expensive to reverse — that is the justification for it being an ADR
rather than a code comment.

## Decision

We will [decision], because [the one or two reasons that actually decided it].

Written as a commitment, in the past tense of the day it was made. Not "we should consider".

## Options considered

### Option A — [name] *(chosen)*

What it is, how it meets the drivers, what it costs.

### Option B — [name]

What it is, and the specific reason it lost. Be fair: if it is better on some dimension, say
which. Rejected options are the most valuable part of this record — they are what stops the
same debate recurring next quarter.

### Option C — [name]

## Consequences

**Positive**
- What becomes easier, cheaper, or safer.

**Negative**
- What becomes harder, more expensive, or riskier. Every real decision has entries here; an
  ADR without them is advocacy, and future maintainers discover the costs anyway — better
  from this document than from an incident.

**Follow-on work**
- What this decision now obliges the team to build, monitor, or document.

## Verification

How anyone can tell this decision is actually in force, and still correct:

| Implemented by | Verified by | Operational control |
|---|---|---|
| Component / code path | Test ID or review | Alert, policy, or gate |

## If this is transitional

Delete unless `state: transition`.

- **Trigger to move on:** an observable threshold, event, or date
- **Compatibility strategy:** what keeps the eventual migration affordable
- **Cost of deferral:** how much harder this gets the longer it waits
- **Migration outline:** how the change happens and how it is verified

## Revisit when

The concrete signal that should reopen this decision — a load threshold, a team change, a
vendor change, a new compliance obligation. Without one, decisions calcify simply because
nobody remembers they were conditional.
