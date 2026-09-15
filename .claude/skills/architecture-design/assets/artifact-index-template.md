---
id: IDX-001
type: architecture
status: draft
state: current
owner:
reviewed: YYYY-MM-DD
review_by: YYYY-MM-DD
sources: []
confidence: confirmed
---

# Architecture artifact index

One findable place listing every artifact, who owns it, and when it is next reviewed. The
most common failure of architecture documentation is not being wrong when written, but that
the author left and nobody inherited it.

`scripts/check_freshness.py` reads the frontmatter of each artifact; this index is the human
view of the same information.

## Artifacts

| ID | Artifact | Type | State | Status | Owner | Reviewed | Review by / trigger |
|---|---|---|---|---|---|---|---|
| ARCH-001 | Architecture | architecture | target | agreed | | | |
| ADR-001 | | adr | | accepted | | | |
| TM-001 | Threat model | threat-model | | draft | | | |

## Deliberately not produced

Recording exclusions is what distinguishes a domain that was considered and set aside from
one nobody looked at. Without this, a reviewer has to reopen all of them.

| Artifact | Why not | Would become necessary if |
|---|---|---|
| Capacity plan | 40 internal users, far inside a single instance | Load grows beyond a few hundred concurrent users |
| RACI | One team owns everything | A second team takes any component |

## Outstanding confirmations

Items still tagged `[unconfirmed]` across all artifacts, pulled together so they can be
cleared in one conversation.

| # | Item | Artifact | Who confirms |
|---|---|---|---|
