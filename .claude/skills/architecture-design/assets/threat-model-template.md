---
id: TM-001
type: threat-model
status: draft
state: target
owner:
reviewed: YYYY-MM-DD
review_by: YYYY-MM-DD
review_trigger: "a new trust boundary, a new data classification, or a new external interface"
sources: [ARCH-001]
confidence: assumed
---

# Threat model — [System name]

Scope: which components and flows this covers, and what it deliberately excludes. An
excluded area that looks covered is the most dangerous output of a threat model.

## 1. Assets worth protecting

| Asset | Classification | Why an attacker wants it | Impact if compromised |
|---|---|---|---|

## 2. Trust boundaries and data flows

```mermaid
%% name: trust-boundaries
flowchart LR
```

Number each flow crossing a boundary; the analysis below references those numbers.

## 3. Threats by boundary (STRIDE)

Repeat per boundary crossing. Delete rows where a threat genuinely does not apply — but say
so rather than leaving the row blank, since a blank reads as "not considered".

### Flow 1 — [source → destination, data carried]

| Threat | Scenario | Mitigation | Where enforced | Verified by | Residual risk |
|---|---|---|---|---|---|
| Spoofing | | | | | |
| Tampering | | | | | |
| Repudiation | | | | | |
| Information disclosure | | | | | |
| Denial of service | | | | | |
| Elevation of privilege | | | | | |

## 4. Abuse cases

Not attacks from outside — misuse by people who are legitimately inside. These are more
common than external attacks and rarely appear in attack-centric models.

| # | Abuse case | Who | Detection | Control |
|---|---|---|---|---|
| AB-1 | Support agent browses records out of curiosity | Staff | Access audit + anomaly alert | Record-scoped access, reviewed monthly |

## 5. Accepted risks

A threat model with no accepted risks usually means nobody thought about it seriously.
Acceptance needs a name and an expiry, or it is not acceptance — it is deferral.

| # | Risk | Why accepted | Accepted by | Expires / revisit |
|---|---|---|---|---|

## 6. Actions

| # | Action | Priority | Owner | Due |
|---|---|---|---|---|
