# Stack, engineering standards, and delivery model

Two domains that look unrelated but decide each other: the technology a team can operate
well depends on the team, and the architecture a team can sustain depends on how the
organisation is shaped.

---

# Part 1 — Technology stack and engineering standards

## 1. Choose for the team and the constraints, then for the technology

The stack decision is usually settled by existing skills, organisational standards, and
what can be hired for — and that is legitimate, not a compromise. A theoretically superior
runtime that one person understands is an operational risk, not an advantage.

Record for each significant technology: what it is used for, why it was chosen, the
alternative that lost, and who has experience with it. Where the choice was made on
non-technical grounds, say so plainly — "the platform team supports .NET on App Service and
nothing else" is a stronger and more honest rationale than a fabricated benchmark.

## 2. Versions and end-of-life

Pin versions and record the support horizon. This is the cheapest possible piece of
foresight and the one most often skipped, and its absence turns into an emergency upgrade
under security pressure.

| Technology | Version | Support ends | Upgrade owner | Notes |
|---|---|---|---|---|
| .NET | 8 (LTS) | Nov 2026 | Platform team | Plan upgrade in Q3 2026 |
| PostgreSQL | 16 | Nov 2028 | Platform team | Managed, minor auto-applied |
| Node | 22 (LTS) | Apr 2027 | Web team | |

Anything already inside its final year of support is a risk register entry today, not a
future problem. State the upgrade cadence: who does it, how often, and whether it can happen
without a product decision — the last is what determines whether upgrades actually occur.

## 3. Standards worth writing down

Only the ones with architectural consequence. Formatting and naming conventions belong to the
team; these belong in the architecture record because other components depend on them:

- **Logging** — structure, required fields, correlation ID propagation, what must never be logged.
- **Error handling** — how errors cross component boundaries, what a client receives, which
  errors are retryable and how a caller can tell.
- **Configuration** — where it comes from, precedence, what is environment-specific, how
  secrets differ from settings.
- **Dependency management** — how new dependencies are approved, how updates are applied,
  what happens with an unmaintained package.
- **API conventions** — pagination, filtering, error format, idempotency headers, versioning.

These matter because they are what let independently built components interoperate without a
negotiation each time.

## 4. Reuse and the paved road

Check what the organisation already provides — an identity platform, a logging pipeline, a
deployment pipeline, a shared component library, an approved base image. Adopting the paved
road is usually right even when the local alternative is slightly better, because the paved
road comes with support, patching, and someone else's on-call.

Deviating is legitimate but is an **exception**: record it as an ADR with the reason, the
cost of the deviation, who approved it, and when it should be reconsidered
(`references/decisions.md`, exceptions section).

## 5. Reproducible development environments

Say how a new engineer gets a working environment, and how long it takes. Containerised or
scripted setup, seeded data, and stubs for external dependencies. Where the answer is "ask
someone", that is a defect worth recording — it silently taxes every onboarding and every
context switch.

## 6. Supply chain

Dependency scanning in CI, lockfiles committed, provenance for base images and build
artifacts, signing where it matters, and a policy for generated code and vendored source.
Name who reviews a new third-party dependency and against what criteria — licence,
maintenance activity, transitive weight, and whether it can be replaced.

---

# Part 2 — Delivery model and team responsibilities

## 7. Match the architecture to the organisation

Systems come to mirror the communication structure of the organisations that build them.
This is not a curiosity; it is a design constraint that should be checked before the
decomposition is finalised.

- **A three-service design owned by one team** usually collapses into a distributed
  monolith — the services deploy together, share a database, and add network calls without
  adding independence.
- **One service owned by three teams** becomes a merge-conflict queue and a release
  negotiation.
- **Independent deployability is only real if a team can decide to deploy** without waiting
  for another team's approval.

So state the intended team-to-component mapping alongside the decomposition, and if the
architecture implies a team structure that does not exist, raise it explicitly. That is an
organisational decision the user may be able to influence — but only if someone names it.

## 8. Ownership matrix

Every component, dataset, environment, and interface needs an owner. "Owner" means: decides
changes, is called when it breaks, and keeps its documentation current.

| Component / asset | Owns | Operates | Tests | Approves change |
|---|---|---|---|---|
| Claims API | Claims team | Claims team | Claims team + QA | Claims tech lead |
| Shared identity integration | Platform | Platform | Platform | Platform + security |
| Claims DB schema | Claims team | Platform | Claims team | Claims tech lead |

Shared ownership without a named decider is the pattern that produces stalled decisions and
unowned incidents. If two teams genuinely share something, name who breaks the tie.

## 9. Skills, staffing, specialists

State what the design requires that the team does not currently have: a skill, a specialist
review (security, accessibility, data protection), or capacity that does not exist. A design
that assumes a Kubernetes expert who has not been hired is a risk register entry with a date,
not a detail to resolve later.

## 10. Decision review without a bottleneck

Architecture review has to be fast enough to keep, or it gets bypassed. Practical shape:

- **Who decides** — the team, for decisions inside their boundary.
- **Who must be consulted** — security for anything touching authentication, data
  classification or external exposure; platform for anything with infrastructure cost.
- **Who is informed** — everyone else, via the ADR itself.
- **Time-box it.** A review that cannot happen within a few days will be routed around,
  and then the decision is made without any record at all.

Record the process in the governance section (see `references/decisions.md`) so exceptions
and escalations have a defined path.

## 11. Artifact ownership after handover

Every artifact gets an owner and a review cadence in its frontmatter, and the artifact index
(`assets/artifact-index-template.md`) lists them in one place. The most common failure of
architecture documentation is not that it was wrong when written, but that the person who
wrote it left and nobody inherited it.

Name the inheriting owner explicitly at handover, and confirm they accept it. An owner who
does not know they are the owner is the same as no owner.

## 12. Artifacts these domains produce

Approved-stack definition, version and end-of-life table, dependency policy, development
environment specification, team topology, component ownership matrix or RACI, decision review
process, and ADRs recording stack choices and exceptions to standards.
