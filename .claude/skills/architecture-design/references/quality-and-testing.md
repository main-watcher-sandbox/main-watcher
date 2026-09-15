# Quality and test architecture

An architecture that cannot be tested cheaply will be tested rarely, and its quality
attributes then exist only on paper. Testability is a design property: the seams, the
injectable dependencies, the ability to run a component in isolation, and the ability to
create realistic data are all decided when the components are drawn.

Two things get skipped almost universally, so decide them explicitly: **who is responsible
for each kind of testing**, and **how the architectural assumptions themselves get tested**.

## 1. Responsibility, stated by name

Ambiguity here means the test does not happen. Everyone assumes someone else covers it, and
the gap surfaces in production.

| Test type | Owner | Runs where | Gate |
|---|---|---|---|
| Unit | Developer | Pre-commit, CI | Merge |
| Component / integration (in-process) | Developer | CI | Merge |
| Contract (provider and consumer) | Owning team, both sides | CI | Merge + provider release |
| End-to-end (critical journeys only) | Team, with QA | Staging | Deploy to prod |
| Exploratory | QA / product | Staging | Release sign-off |
| Accessibility | Frontend + QA | CI (automated) + manual audit | Release |
| Performance | Team + platform | Pre-prod, production-like | Release, and on change to critical path |
| Resilience / chaos | Platform + team | Staging, then production carefully | Periodic |
| Security (SAST, dependency, pen test) | Security + team | CI; pen test per release cycle | Release |
| Disaster recovery (restore, failover) | Platform | Scheduled drill | Quarterly |
| User acceptance | Product owner / users | Staging | Release |

Fill this in with real roles. "The team" everywhere means nobody in particular, and the
tests that get dropped under pressure are always the ones with the vaguest ownership.

## 2. Shape of the suite

Favour many fast tests close to the code and few slow ones far from it — not out of purity,
but because a suite that takes 40 minutes stops being run before merge, and a suite people
skip provides no protection at all.

- **Unit** — pure logic, domain rules, edge cases. Milliseconds.
- **Component** — one deployable unit with its real database, dependencies stubbed. This is
  where most bugs are actually caught in service-shaped systems.
- **Contract** — provider verifies it satisfies the published schema; consumers verify their
  expectations. Catches integration drift without a full environment.
- **End-to-end** — a handful of critical journeys only. Expensive, slow, and flaky in
  proportion to how many you have; each one needs to earn its place.

Where end-to-end tests are the only thing catching a class of bug, that is usually a signal
that a seam is missing in the design.

## 3. Test data and environments

Test data is where compliance and testing collide. Copying production data into a lower
environment is a data-protection decision (see `references/security.md`), not a convenience.
The realistic options: synthetic generation, masked or subsetted extracts with approval, or
seeded fixtures per test.

Each has a cost: synthetic data misses the weird real-world cases that cause defects; masked
data risks incomplete masking; fixtures drift from reality. Choose deliberately and record it.

Environments should be listed with what each is for, how production-like it is, who can
deploy to it, and how it is refreshed. Ephemeral per-branch environments are worth their
setup cost where integration risk is high.

## 4. Simulating dependencies

For each external dependency, say how it is stood in for: vendor sandbox, a simulator you
run, recorded fixtures, or a stub. Note that **sandboxes drift from production behaviour** —
particularly around error responses, rate limits and timeouts, which are exactly the paths
resilience design depends on. A periodic test against the real dependency, even if only a
smoke test, is what catches that drift before an incident does.

## 5. Quality gates

State what must pass at each point, and who may override:

| Gate | Requires |
|---|---|
| Merge | Unit + component pass, coverage not decreased, SAST clean, dependency scan clean |
| Deploy to staging | Contract tests pass, migrations applied cleanly |
| Deploy to production | Critical E2E pass, performance within budget, no open critical vulnerabilities |
| Release / enable | Accessibility check, runbook exists, alerts configured, rollback verified |

Gates nobody may override are ignored under pressure; gates anyone may override silently are
decoration. Name who can override and require the override to be recorded — that keeps the
gate meaningful and the exception visible.

## 6. Testing the architecture itself

This is the part specific to architecture work, and the part most often missing. Each
significant quality attribute claim should have a test that would fail if the claim were
false:

| Claim | Test | When |
|---|---|---|
| p95 submit under 3s on 3G | Load test at 2× peak, throttled network profile | Pre-launch, then on critical-path change |
| Survives AZ loss | Kill an AZ in staging, confirm recovery within RTO | Quarterly |
| RPO of 15 minutes | Restore from backup, measure data loss | Quarterly drill |
| Tenant isolation holds | Automated test attempting cross-tenant access | Every build |
| Retry cannot double-charge | Duplicate submission test with same idempotency key | Every build |
| Deletion reaches backups | Erasure test including derived stores | Per release |

Where a claim has no test, label it `[assumption]` in the architecture document rather than
stating it as fact. That is honest, and it puts the gap somewhere a reviewer can see it.

## 7. From defect to regression

Every production defect above a chosen severity becomes a test at the lowest level that
would have caught it. This is what stops the same failure recurring and steadily moves the
suite toward the risks the system actually has, rather than the ones its authors imagined.

Record the rule and the owner in the test strategy, because it only happens if someone is
accountable for it when the incident is over and everyone wants to move on.

## 8. Artifacts this domain produces

Test strategy (`assets/test-strategy-template.md`), test responsibility matrix, environment
matrix, quality gate definitions, and requirement-to-test traceability — the last of which
can be generated from requirement IDs referenced in test names or files, in the same way
`scripts/check_coverage.py` traces requirements to design.
