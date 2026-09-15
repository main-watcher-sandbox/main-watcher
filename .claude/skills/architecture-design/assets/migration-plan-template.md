---
id: MP-001
type: migration-plan
status: draft
state: transition
owner:
reviewed: YYYY-MM-DD
review_by: YYYY-MM-DD
review_trigger: "any transition state lasting beyond its planned window"
sources: [ARCH-001]
confidence: assumed
---

# Migration plan — [from] to [to]

## 1. Why

The drivers forcing this change, and what happens if it does not happen. Migrations lose
funding halfway; a written reason is what survives a budget conversation.

## 2. States

```mermaid
%% name: transition-states
flowchart LR
```

| State | What exists | Duration | Trigger to leave | Rollback from here |
|---|---|---|---|---|
| Current | | — | | n/a |
| Transition 1 | | | | |
| Target | | — | | |

Each transition state runs in production, sometimes for months. It gets the same scrutiny as
the target.

## 3. Data migration

| Dataset | Volume | Transform | Verification | Downtime | Reversible? |
|---|---|---|---|---|---|

"We ran the script" is not verification. Name the check: row counts, checksums,
reconciliation report, sampled manual review.

## 4. Parallel running

Whether both systems run at once, which is authoritative meanwhile, who reconciles
differences and how often, and what happens when they disagree.

## 5. Cutover

Approach (big bang, phased by capability, phased by user group, strangler routing), the
sequence, the go/no-go criteria, and who decides.

**Point of no return:** the moment rollback stops being possible — usually when users create
data in the new system. State it explicitly and what the contingency is after it.

## 6. Compatibility during transition

| Interface / format | Old supported until | Consumers | How they are told |
|---|---|---|---|

## 7. Retirement of the legacy system

The step funded last and skipped most often.

| # | Action | Owner | Date |
|---|---|---|---|
| 1 | Confirm no remaining dependencies (logs, traffic, scheduled jobs) | | |
| 2 | Export / archive data per retention obligations | | |
| 3 | Switch off, keep read-only archive for [period] | | |
| 4 | Cancel licences, infrastructure, monitoring, on-call rotation | | |

## 8. Risks

| # | Risk | Impact | Mitigation | Owner |
|---|---|---|---|---|
