---
id: TS-001
type: test-strategy
status: draft
state: target
owner:
reviewed: YYYY-MM-DD
review_by: YYYY-MM-DD
sources: [ARCH-001]
confidence: assumed
---

# Test strategy — [System name]

## 1. What this system must be trusted to do

The two or three failures that would actually matter — the ones testing exists to prevent.
Everything below should trace back to one of them.

## 2. Responsibility matrix

Ambiguity here means the test does not happen. Name real roles, not "the team".

| Test type | Owner | Runs where | Gate | Notes |
|---|---|---|---|---|
| Unit | | Pre-commit, CI | Merge | |
| Component / integration | | CI | Merge | |
| Contract | | CI | Merge + provider release | |
| End-to-end (critical journeys) | | Staging | Deploy to prod | |
| Exploratory | | Staging | Release | |
| Accessibility | | CI + manual audit | Release | |
| Performance | | Pre-prod | Release + critical-path change | |
| Resilience / DR drill | | Staging, then prod carefully | Quarterly | |
| Security (SAST, deps, pen test) | | CI + per cycle | Release | |
| User acceptance | | Staging | Release | |

## 3. Environments and test data

| Environment | Purpose | Production-like? | Data source | Refresh | Who can deploy |
|---|---|---|---|---|---|

Copying production data into a lower environment is a data-protection decision, not a
convenience. State the masking or synthesis approach and who approved it.

## 4. Simulating dependencies

| Dependency | Stood in by | Drift risk | Real-dependency check |
|---|---|---|---|

Sandboxes drift from production behaviour, especially on errors, rate limits and timeouts —
exactly the paths the resilience design relies on.

## 5. Quality gates

| Gate | Must pass | Who may override | Override recorded where |
|---|---|---|---|

## 6. Testing the architecture itself

Each quality attribute claim needs a test that would fail if the claim were false. Claims
without one are labelled `[assumption]` in the architecture document rather than stated as
fact.

| Claim | Requirement | Test | Frequency | Status |
|---|---|---|---|---|

## 7. Defect to regression

Every production defect at or above [severity] becomes a test at the lowest level that would
have caught it. Owner: [role]. This is what steadily moves the suite toward the risks the
system actually has.

## 8. Traceability

| Requirement | Covered by |
|---|---|
