# Question bank — all fourteen domains

Pull only from the domains the risk screen in SKILL.md put in play, and within a domain
only the questions whose answers would change a decision. The bank is deliberately larger
than any single interview should be.

Each domain lists the questions, then **why it matters** — use that to explain the purpose
of a question when you ask it, and to decide when the domain can be skipped entirely.

## Contents

1. [Context, scope, drivers](#1-context-scope-and-drivers) · 2. [Structure and boundaries](#2-structure-and-boundaries) ·
3. [Domain and data](#3-domain-and-data) · 4. [Identity and security](#4-identity-and-security) ·
5. [Integration and dependencies](#5-integration-and-dependencies) · 6. [Stack and standards](#6-stack-and-engineering-standards) ·
7. [Deployment and runtime](#7-deployment-and-runtime) · 8. [Quality and testing](#8-quality-and-testing) ·
9. [Performance and resilience](#9-performance-scalability-resilience) · 10. [Observability and operations](#10-observability-and-operations) ·
11. [Delivery model and teams](#11-delivery-model-and-teams) · 12. [Evolution and migration](#12-evolution-and-migration) ·
13. [Cost and licensing](#13-cost-licensing-sustainability) · 14. [Governance](#14-governance-and-decisions)

---

## 1. Context, scope and drivers

- What problem is this solving, and what happens if it is not solved?
- Who are the users, and who are the stakeholders, operators and decision-makers (often different people)?
- What is explicitly out of scope for this phase, and where is that work tracked?
- Which business capabilities and processes does this touch?
- What existing systems or processes will be replaced, extended, or left alone?
- Which quality attributes actually matter here, in priority order — security, availability,
  performance, scalability, usability, maintainability, interoperability, portability, cost?
- For each of the top three, what is the measurable target and under what conditions?

**Why it matters:** without a ranked quality-attribute list, every trade-off later becomes
an argument about preference rather than a comparison against an agreed goal. Force the
ranking — "if you could only have one of availability or cost, which?" — because a
stakeholder who says everything is critical has not yet made a decision.

---

## 2. Structure and boundaries

- What are the major capabilities, and do any of them have genuinely different rates of change?
- Which parts need to be deployable independently, and why — different teams, different
  release cadence, different scaling profile?
- Where are the ownership boundaries between teams?
- Which interactions must be synchronous, and which can be asynchronous?
- What failures must be isolated from each other?
- Is anything expected to be reused by other systems or replaced wholesale later?

**Why it matters:** decomposition is the decision people get wrong most expensively, in both
directions. Independent deployability is the only justification for a service boundary that
holds up; system size, tidiness, and fashion are not.

---

## 3. Domain and data

- What are the principal domain concepts, and which business rules must always hold?
- Who owns each category of data — which system is authoritative, and what is derived,
  cached, or replicated?
- What consistency does each operation actually need: transactional, read-your-writes, or eventual?
- What are the identifiers, and are any of them externally meaningful (invoice numbers, account codes)?
- How is data classified (public, internal, personal, sensitive personal, regulated)?
- Residency: must data stay in a jurisdiction?
- Retention, archival, deletion, legal hold — how long, and who can trigger erasure?
- What are the expected volumes and growth per entity?
- How will schema change be handled once there is production data?
- Backup, restore, and disaster recovery expectations — RPO and RTO?
- Is the initial choice transitional (SQLite now, PostgreSQL later)? What triggers the move,
  and how is compatibility preserved across it?

**Why it matters:** data outlives every other decision. Application code is rewritten
routinely; the data model and its history are what a replacement system has to inherit.
Idempotency and concurrency questions belong here too, because they are properties of the
data model far more than of the code.

---

## 4. Identity and security

- Who and what authenticates — users, services, devices, administrators — and by what protocol?
- Is there an existing identity provider, or does one need choosing?
- Is authorisation role-based, attribute-based, or per-record and tenant-scoped?
- If multi-tenant: must isolation be physical, or is a tenant column acceptable, and who says so?
- How are sessions, tokens, service identities, secrets, keys and certificates issued and rotated?
- Where are the trust boundaries, and what is the attack surface at each?
- How is data protected in transit and at rest, and who holds the keys?
- What security events must be audited, retained how long, and reviewed by whom?
- Which regulations or organisational policies apply (GDPR, PIPEDA, HIPAA, PCI-DSS, SOC 2, sector rules)?
- How are vulnerabilities, dependency scanning, patching and penetration testing handled?
- What abuse cases matter — not just outsider attack, but insider misuse and automated abuse?
- Who responds to an incident, and what is the disclosure obligation?

**Why it matters:** these are the least negotiable constraints available, and retrofitting
them costs multiples of building them in. Ask early even when the answer feels obvious —
"it's internal only" collapses the moment someone asks for contractor access.

---

## 5. Integration and dependencies

- What must this talk to, and which of those can be changed if needed?
- For each: who owns it, and what service-level commitment exists — contractual or hoped-for?
- Protocol, message format, contract, and versioning policy for each interface?
- Who initiates, and is it request/response, batch, or event stream?
- What are the real rate limits, quotas, and payload limits?
- What happens to us when each dependency is slow, wrong, or down — and for how long is that tolerable?
- How are timeouts, retries, backoff, circuit breaking, deduplication and partial failure handled?
- How will breaking changes be communicated and absorbed?
- Is an exit strategy or substitute required for any critical vendor?
- What licences, costs, geographic restrictions or contractual constraints apply?

**Why it matters:** integrations cause a disproportionate share of production incidents and
schedule slips, because their failure modes are outside your control and usually undocumented.
A dependency without a stated failure behaviour is an outage waiting to be discovered in production.

---

## 6. Stack and engineering standards

- Which languages, frameworks, runtimes, datastores, messaging and infrastructure technologies are proposed?
- Why each, and what was the alternative that lost?
- Which versions, and what is the support horizon — is anything already near end-of-life?
- Does the organisation have an approved stack, a paved road, or reusable platform capabilities to adopt?
- What standards apply for logging, error handling, configuration and dependency management?
- How is a local development environment made reproducible?
- How are third-party packages, generated code and supply-chain risk governed?
- What is the upgrade and deprecation policy — who does it, and when?

**Why it matters:** the stack decision is usually settled by team skills and organisational
standards rather than technical merit, and that is legitimate. Make it explicit so nobody
relitigates it, and so the end-of-life exposure is visible before it becomes urgent.

---

## 7. Deployment and runtime

- Where does this run — existing cloud account, on-premises, customer-hosted, edge, desktop?
- Which environments are needed, and how production-like must each be?
- How are infrastructure and configuration provisioned, and is that code?
- What are the network zones, trust boundaries, ingress and egress paths?
- How are builds, artifacts, containers and releases produced, signed and stored?
- What deployment strategy — rolling, blue-green, canary, feature flags?
- How are database changes coordinated with application releases?
- Does the application update itself? If so: how are updates signed, staged, observed,
  paused and rolled back, and what happens to a client that skips several versions?
- How are environment configuration and secrets supplied at runtime?
- What are the availability, geographic distribution, scaling and disaster-recovery expectations?

**Why it matters:** the deployment model determines how fast a fix reaches production, which
is the single largest factor in how bad an incident becomes. Self-updating software deserves
special attention because a bad auto-update can break every install simultaneously.

---

## 8. Quality and testing

- Who is responsible for testing each part — developers, a test team, security, platform, product, users?
- Which test types are required: unit, component, contract, integration, end-to-end,
  exploratory, accessibility, performance, resilience, security, disaster recovery?
- What must be automated, and where does each suite run?
- How are test environments and test data created, refreshed and governed — especially where
  production data is personal?
- How are external dependencies simulated, virtualised or sandboxed?
- What quality gates must pass before merge, before deploy, before release?
- How will the architectural assumptions themselves be tested (the load target, the failover, the restore)?
- How does a production defect become a regression test?

**Why it matters:** an architecture that cannot be tested cheaply will be tested rarely, and
its quality attributes are then aspirations. The most commonly skipped test is the one that
proves the resilience design works — an untested failover is a hypothesis.

---

## 9. Performance, scalability, resilience

- What are the expected workloads, volumes, concurrency and latency targets, and at what percentile?
- What does the peak look like relative to the average, and is it predictable?
- What growth is expected over 12–24 months?
- Where do you expect the bottleneck to be, and how would you know?
- What is the scaling approach — vertical, horizontal, partitioned — and what is the ceiling?
- Where do caching, batching, queueing, backpressure or load shedding apply?
- Which failures are expected, and what does graceful degradation look like for each?
- What are the availability target, RTO and RPO — and are they written down anywhere binding?
- How are retry storms, cascading failures, duplicate processing and network partitions handled?
- How will these assumptions be validated before launch, not after?

**Why it matters:** most systems are far smaller than their designers assume, so the goal is
usually to find the cheapest design that provably meets a written target — not to maximise
throughput. A number nobody validated is not a requirement, it is a hope.

---

## 10. Observability and operations

- What logs, metrics, traces, audit records and business signals are needed, and by whom?
- How do correlation IDs propagate across components and into third parties?
- What are the service-level indicators and objectives, and who agreed them?
- What alerts exist, who receives them, and at what hour?
- Who owns production incidents, and what is the escalation path?
- What dashboards, runbooks and health checks must exist before launch?
- How is production access granted, scoped and audited?
- Which operational actions can be automated, and which must stay manual and why?
- How will supportability be tested before release — can someone diagnose a fault using only
  what the system emits?

**Why it matters:** operability is a design property, not an afterthought. If the design
makes an incident undiagnosable at 3am, that is an architecture defect, and it is cheapest
to fix while the components are still being drawn.

---

## 11. Delivery model and teams

- Which teams own which capabilities, components, data, infrastructure, testing, security, operations?
- Where are handoffs, and where is ownership genuinely shared (usually a warning sign)?
- Does the proposed architecture match the team structure that will build and run it?
- What skills, staffing, training or specialist support are missing?
- How do architecture decisions get reviewed and approved without becoming a bottleneck?
- Who keeps each artifact current after this project ends?

**Why it matters:** systems come to mirror the communication structure of the organisations
that build them. A three-service design owned by one team tends to collapse into a
distributed monolith; one service owned by three teams becomes a merge conflict. Check the
match explicitly rather than hoping.

---

## 12. Evolution and migration

- What is the current architecture, and what is the target?
- What transitional states are expected, and how long will each last?
- Which decisions are explicitly temporary, and what triggers moving past each?
- How will legacy systems and data be migrated, verified, and retired?
- Will old and new run in parallel? Who reconciles them, and for how long?
- How is backward compatibility maintained during transition — for data, APIs, and clients?
- How are technical debt and architecture risk recorded and prioritised?
- What business, scale, regulatory or technology change is anticipated, and does the design accommodate it?
- What is the plan for version upgrades, deprecation, end-of-support, data export and retirement?

**Why it matters:** almost nothing is greenfield. The transition is usually harder than
either end state, and the parallel-run period is where the real cost and the real risk sit.
A target architecture with no transition plan is a wish.

---

## 13. Cost, licensing, sustainability

- What are expected build and run costs, and against what budget?
- What drives cost as usage grows — compute, storage, egress, per-seat licensing, per-request pricing?
- What budgets, quotas and cost alerts should exist?
- What licences apply to software and to data, and do any restrict commercial use or redistribution?
- Where is the lock-in, and what would switching actually cost?
- Where is the obvious waste — over-provisioned environments, retained data nobody reads?
- How will actual cost be compared against the assumptions in this design?

**Why it matters:** cost decides managed-versus-self-hosted more often than any technical
argument, and unit economics that only break at scale are invisible until they are
expensive. Ask what drives the bill, not just what the bill is.

---

## 14. Governance and decisions

- Which decisions are significant enough to record, and who decides that?
- Who approves an architecture decision, and who must merely be informed?
- How are exceptions to standards requested, granted, time-boxed and tracked?
- When should each decision be reviewed, and on what trigger?
- Which requirements, diagrams, code, tests, work items and operational controls implement or verify each decision?
- How will documentation contradicted by the implementation be detected?
- Who owns each artifact after handover?

**Why it matters:** the governance answers determine whether any of the rest survives
contact with a year of delivery pressure. An architecture record with no owner and no review
trigger becomes archaeology within two release cycles.
