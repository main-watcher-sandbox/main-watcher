---
name: architecture-design
description: Guided, risk-based architecture design and documentation. Interrogates the user, proposes genuinely different options with trade-offs, confirms each decision, then generates only the artifacts the project warrants — architecture docs, Mermaid diagrams (context, container, component, sequence, class, data-flow, state, deployment, network, trust boundary), ADRs, threat models, test strategy, migration plans, and requirement-to-design traceability. Use whenever the user mentions architecture, system design, solution design, technical design docs, HLD/LLD, C4, ADRs, threat modelling, or asks for component, sequence, class, deployment, network or data-flow diagrams — and for "how should we build this", "what stack should we use", documenting, reviewing or updating an existing system's architecture, planning a migration between architectural states, or continuing from user stories into design. Prefer this skill over answering a design question directly, even when the request sounds casual.
---

# Architecture design and documentation

Stories say what the system must do for people. Architecture is decided mostly by what they
leave out — load, data sensitivity, integrations, team shape, deployment target, budget —
and by which trade-offs the team will accept. So the work is not "write a document". It is:
recover the missing context, put the real choices in front of the user, get a decision on
each, produce only the artifacts this project warrants, and keep them honest over time.

## The sequence

**1. Ground in the sources → 2. Triage risk → 3. Interview → 4. Select artifacts →
5. Put options on the table → 6. Get an explicit decision on each → 7. Generate →
8. Validate → 9. Maintain.**

Steps 5 and 6 are where the value is. Do not skip to generation because the user seems to
be in a hurry; compress steps 1–6 instead, state assumptions loudly, and give them
something concrete to correct.

---

## Two conventions that apply to everything

These are what separate a maintainable architecture record from a document that quietly
rots. Apply them to every artifact, in every mode.

### Convention 1 — Label the epistemic status of every claim

A reader cannot act on a document that mixes verified fact with plausible invention, nor
tell what is safe to change. So distinguish, explicitly:

| Status | Means | Must carry |
|---|---|---|
| **Fact** | Verified from a source | The source (file, ticket, person, measurement) |
| **Constraint** | Non-negotiable input | Who imposes it, and whether it is legal, contractual or technical |
| **Assumption** | Believed, not verified | Impact if wrong, owner, how it gets confirmed |
| **Decision** | Agreed and binding | ADR reference |
| **Recommendation** | Proposed, not yet agreed | The reasoning, and what would change it |
| **Risk** | Might happen, would hurt | Impact, likelihood, mitigation, owner |
| **Open question** | Unresolved | The default assumed meanwhile, owner, by when |

In prose, tag anything whose status is not obvious from context: `[assumption]`,
`[recommendation]`, `[open]`. Every artifact also carries registers — Assumptions, Risks,
Open questions, Decisions — so a reader can find all of them without reading the whole
document.

**Anything generated without user confirmation is marked `[unconfirmed]`** and appears in a
confirmation queue near the top of the artifact. Inventing a plausible requirement and
presenting it in the same voice as a confirmed one is the most damaging thing this skill
can do, because it launders a guess into an authority a team then builds on.

### Convention 2 — Every artifact carries ownership and a review trigger

Every generated file starts with YAML frontmatter. This is what makes the record
mechanically checkable — `scripts/check_freshness.py` reads it — and what stops artifacts
outliving their truth.

```yaml
---
id: ADR-007
type: adr            # architecture | adr | threat-model | test-strategy | migration-plan | review
status: proposed     # draft | proposed | agreed | accepted | superseded | retired
state: target        # current | target | transition
owner: platform-lead            # a role or a person, never blank
reviewed: 2026-08-18
review_by: 2027-02-18           # a date, a trigger, or both
review_trigger: "sustained load above 200 req/s, or a second team takes ownership"
sources: [US-001, NFR-004, ADR-002]
confidence: assumed             # confirmed | assumed | unverified
supersedes: ADR-003
---
```

A decision that is **transitional** — SQLite now and PostgreSQL later, a monolith that will
be split, a manual process to be automated — is not a lesser decision. It is a decision
with an expiry, and it must carry the trigger that ends it. "We'll fix it later" without a
trigger is how a temporary choice becomes permanent by accident.

---

## 1. Ground in the sources

Read the stories, requirements, existing docs and, where available, the code before asking
anything. A user asked something the backlog already answers will reasonably conclude you
did not read it.

Extract a **driver table** — the things that will actually shape the design. Non-functional
requirements dominate: latency, availability, retention and compliance constrain a design
far more than any feature does.

| Driver | Type | Source | Consequence for the design |
|---|---|---|---|
| Search results under 2s at p95 | Fact | NFR-004 | Read model or search index, not table scans |
| Card data never stored | Constraint | PCI-DSS | Tokenisation; gateway holds the number |
| ~40 internal users, flat growth | Assumption | Interview, unverified | One deployable service is sufficient |

Keep the IDs. Where the backlog came from the `user-stories` skill it already uses `US-`,
`FR-`, `NFR-`, `EP-`; carry them through so every component traces to a reason for
existing, and `scripts/check_coverage.py` can prove it.

Confirm the driver table before going further. It is cheap to correct now and expensive
after six decisions rest on it.

## 2. Triage risk — decide which domains are actually in play

Fourteen concern areas exist. Most projects genuinely engage five or six. Working all of
them produces an unread document and an exhausted user, so screen first: if a trigger is
false, say so once and move on.

| # | Domain | In play when | Read before working it |
|---|---|---|---|
| 1 | Context, scope, drivers | Always | `references/discovery.md` |
| 2 | Structure and boundaries | Always | `references/decisions.md` |
| 3 | Domain and data | Anything is persisted | `references/data-and-domain.md` |
| 4 | Identity and security | Personal, regulated or multi-tenant data; external exposure | `references/security.md` |
| 5 | Integration and dependencies | Anything is called that you do not control | `references/integration.md` |
| 6 | Stack and engineering standards | New stack, or org standards to comply with | `references/stack-and-delivery.md` |
| 7 | Deployment and runtime | It runs somewhere non-trivial, or self-updates | `references/runtime.md` |
| 8 | Quality and testing | Anyone other than the author will verify it | `references/quality-and-testing.md` |
| 9 | Performance, scale, resilience | A stated NFR, real load, or a failure that costs money | `references/runtime.md` |
| 10 | Observability and operations | It runs unattended, or someone is on call | `references/runtime.md` |
| 11 | Delivery model and teams | More than one team, or unclear ownership | `references/stack-and-delivery.md` |
| 12 | Evolution and migration | Something is being replaced, or a decision is transitional | `references/evolution.md` |
| 13 | Cost, licensing, sustainability | A budget ceiling, usage-based pricing, or lock-in | `references/evolution.md` |
| 14 | Governance and decisions | Always | `references/decisions.md` |

Tell the user which domains you judged in play and which you set aside, in a line or two.
Setting a domain aside is itself a recorded judgement someone may want to challenge — an
unexamined domain and a deliberately excluded one look identical in the final document
unless you say which is which.

`references/question-bank.md` holds the questions for all fourteen domains, with why each
matters. Pull only from the domains that passed the screen.

## 3. Interview

Ask only the questions whose answers change a decision. A long questionnaire gets abandoned
or answered carelessly, which is worse than not asking.

- **Open with what you inferred, not a blank form.** Reacting is easier than generating.
- **Batch by theme**, five to eight per round, two or three rounds.
- **Offer candidate answers.** "Peak concurrent users: under 100, hundreds, or tens of
  thousands?" beats "what are your scalability requirements?".
- **Attach a default to every question** so a busy user can say "defaults are fine" and
  still get a competent design.
- **Say why you are asking** — users often volunteer the constraint you did not know to ask
  about.
- **Never block.** Missing answer → reasonable default, recorded as `[assumption]` with an
  owner, not buried in prose.

`references/discovery.md` covers running the interview and mining stories for actors,
entities, invariants and integrations.

## 4. Select the artifacts

Recommend only artifacts this project warrants. Every artifact has an ongoing maintenance
cost, and an unmaintained artifact is worse than none because people trust it until it
burns them.

Propose a short list with a one-line justification each, tied to a driver or a risk, and let
the user cut it. Default floor for almost any project: an architecture document with context
and container views, ADRs for the significant decisions, and a risk register. Everything
else — threat model, data-flow diagrams, capacity plan, test strategy, migration plan,
RACI, cost model — earns its place by a specific risk.

`references/artifact-selection.md` maps risk signals to artifacts and gives the heuristics.

## 5. Put options on the table

Identify the **architecturally significant decisions**: expensive to reverse, hard to hide
behind an interface, or visible to a stakeholder. Usually three to six. Raising fifteen
produces fatigue and a rubber stamp.

For each, offer two or three options differing in *kind*, not degree, that a competent
engineer would defend. Always include the simple option — the modular monolith, the single
database, the managed service, the thing the team already runs. It is frequently correct,
and where it is not, showing why is the strongest argument for the alternative.

Score against the drivers from step 1, not generic virtues. Keep a reversibility row. Then
**recommend one and say what would change your mind**. Refusing to recommend is not
neutrality; it hands the work back to the person who asked.

Use `assets/decision-brief-template.md` beyond one or two decisions.
`references/decisions.md` covers option-set construction, trade-off tables, transitional
decisions, exceptions to standards, and a map of common decisions to their real options.

## 6. Get an explicit decision on each

Confirm **decision by decision**, not with one "does this look OK?" at the end. Users agree
to a wall of text far more readily than to six specific things, and the first kind of
agreement does not survive review.

Make acceptance easy: "Unless you object I'll go with A, B and D — the two I'd expect
pushback on are B and D." If the user decides against your recommendation, record *their*
decision and note the trade-off in the ADR's consequences. If a new constraint appears late,
reopen the affected decisions explicitly rather than patching around them. Deferring is
legitimate when recorded with the trigger that ends the deferral.

## 7. Generate

Size the output to the project. An over-structured document for a two-week build goes
unread; a thin one for a regulated platform fails its first review.

| Tier | When | Contents |
|---|---|---|
| **Lightweight** | Small scope, one team, few integrations, low compliance | One `architecture.md`: overview, drivers, container view, one or two sequences, decisions, risks. Two to four diagrams. |
| **Standard** | Multiple components or integrations, several teams, a real ops surface | Sectioned `architecture.md` + separate ADRs + data and deployment views + test strategy. Five to eight diagrams. |
| **Comprehensive** | Regulated, safety-critical, long-lived, or handed to another organisation | Full section set, threat model, capacity plan, migration plan, ownership matrix, ADR per significant decision. |

Where current and target states differ, keep them as **separate artifacts** with
`state: current` and `state: target`, plus a transition plan naming the intermediate states.
Blending them into one document produces a picture that describes nothing that exists — see
`references/evolution.md`.

Start from `assets/architecture-doc-template.md` and `assets/adr-template.md`, plus the
specialised templates when their domain is in play: `assets/threat-model-template.md`,
`assets/test-strategy-template.md`, `assets/migration-plan-template.md`, and
`assets/artifact-index-template.md`. Delete sections you have nothing real for; an empty
heading implies a question was considered when it was not.

Default layout, adapted to whatever the repo already uses:

```
docs/architecture/
├── architecture.md              # main document (state: current or target)
├── artifact-index.md            # every artifact, owner, review date, status
├── decisions/ADR-001-*.md
├── threat-model.md              # if domain 4 is in play
├── test-strategy.md             # if domain 8 is in play
├── migration-plan.md            # if domain 12 is in play
├── diagrams/                    # generated svg + png
└── traceability.md              # generated
```

### Diagrams

Diagrams live as ```mermaid blocks inside the markdown so they stay diffable and render in
a pull request. Give each a `%% name: container-view` first line — the render script uses it
for the filename.

Choose views by the question being answered. Label every arrow with what flows and over what
protocol, and use the same names everywhere — container view, sequences, ADRs, code. Read
`references/diagram-catalog.md` first: it has validated Mermaid for each view type
(including data-flow, trust-boundary, dependency and capability maps), layout rules that
keep diagrams legible, and the syntax pitfalls that break rendering.

## 8. Validate

```bash
python3 scripts/render_diagrams.py docs/architecture/ --check     # diagram syntax
python3 scripts/render_diagrams.py docs/architecture/             # export svg + png
python3 scripts/check_coverage.py --stories backlog/ --arch docs/architecture/ \
    --matrix docs/architecture/traceability.md                    # requirement coverage
python3 scripts/check_consistency.py docs/architecture/           # contradictions, ADR completeness, naming
python3 scripts/check_consistency.py docs/architecture/ --json report.json   # same, for CI
python3 scripts/check_freshness.py docs/architecture/             # owners, review dates, unconfirmed items
```

**Look at at least one rendered image.** Parsing proves the syntax is legal, not that the
diagram is readable — Mermaid places edge labels automatically and they collide in busy
flowcharts. Section 9b of the diagram catalog has the layout fixes.

Then run the judgement-based review scripts cannot do — completeness, feasibility, security,
operability, testability, and contradictions between artifacts. The checklist and the common
contradiction patterns are in `references/review-and-validation.md`. Do this before handing
over, and report what you found rather than quietly fixing it, so the user learns where the
design is thin.

Close by stating: what was produced, the decisions recorded, the assumptions still needing
confirmation, the top risks, and what you did *not* cover. Do not restate the document.

## 9. Maintain

Architecture documents fail by drifting from the system, not by being wrong on day one.
When artifacts already exist, **update them incrementally — never regenerate**:

- Read what exists first, including frontmatter, ADR history and registers.
- Change only what the new information actually changes. Wholesale regeneration destroys
  provenance, silently reverses earlier decisions, and makes review impossible because every
  line shows as modified.
- A reversed decision means a **new ADR superseding the old one**. Never edit a decided ADR
  to say something different; the history is the point.
- Update `reviewed:` when you have actually re-examined an artifact, not when you touch it.
- Resolve confirmation-queue items as answers arrive; drop `[unconfirmed]` only on
  confirmation.
- Re-run the validation scripts and report what changed.

`references/evolution.md` covers transitional decisions, migration and transition
architectures, technical-debt registers, and decommissioning.

---

## Adapting to the situation

**No stories, just an idea.** Same work, softer drivers. Ask for the two or three headline
capabilities and who uses them, then proceed. Offer the `user-stories` skill once if a real
backlog would help — but do not refuse to design without one.

**Documenting an existing system.** Skip option generation; there is nothing to choose.
Interrogate the current design instead — read the code and config where available — write it
up as `state: current`, and record decisions already implicit in the implementation as ADRs
with status `accepted`, noting they are retrospective. Where the existing design conflicts
with current drivers, that list is usually the most valuable output.

**Reviewing someone else's architecture.** Go straight to
`references/review-and-validation.md`. Produce a review artifact with findings ranked by
severity, each tied to a driver or a risk, not to preference.

**"Just give me the document."** Compress rather than skip. Make every assumption explicit,
mark unilateral decisions `[unconfirmed]`, and lead with "here are the four things I assumed
— any of them wrong changes the design."

**Mid-conversation continuation.** Harvest the decisions already made rather than re-asking,
and confirm the list before writing.

**Only one part is in question.** Scope to it. A decision brief plus one sequence diagram is
a complete deliverable when the question is "how should these two services talk?".

---

## Anti-patterns

| Anti-pattern | Why it hurts | Instead |
|---|---|---|
| Writing the document before agreement | Presents guesses with the authority of a finished artefact | Decide first, document second |
| Unlabelled invention | A guess becomes a requirement the team builds on | `[unconfirmed]` plus a confirmation queue |
| Strawman options | Reviewers spot it instantly and stop trusting the document | Options a competent engineer would defend |
| Producing every artifact in the catalogue | Unmaintained artifacts are trusted until they burn someone | Select against risk; justify each one |
| "Temporary" with no trigger | Transitional choices become permanent by accident | A date, a threshold, or both |
| Regenerating docs on update | Destroys provenance and hides reversals | Incremental edits; new ADR supersedes old |
| Artifacts with no owner | Nobody updates what nobody owns | Owner and review date in frontmatter |
| One document mixing current and target state | Describes a system that does not exist | Separate artifacts plus a transition plan |
| ADRs listing only benefits | Reads as advocacy; costs surface in an incident | Write negative consequences plainly |
| Distributing a system nobody measured | The most expensive reversible-in-theory mistake | Simplest thing meeting a written-down driver |
| Components with no requirement behind them | Speculative work nobody asked for | Every component cites a requirement |
