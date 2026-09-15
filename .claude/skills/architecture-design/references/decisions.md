# Options, trade-offs, and decision records

The value of proposing options is that it forces the trade-off into the open before it is
buried in code. The failure mode is theatre: two obviously-bad options either side of the
one already chosen. A reviewer can spot a rigged option set instantly, and it destroys
trust in the rest of the document.

## Contents

1. [Finding the decisions worth deliberating](#1-finding-the-decisions-worth-deliberating)
2. [Building an honest option set](#2-building-an-honest-option-set)
3. [Trade-off analysis](#3-trade-off-analysis)
4. [Reaching consensus](#4-reaching-consensus)
5. [Writing the ADR](#5-writing-the-adr)
6. [Common decisions and their real option sets](#6-common-decisions-and-their-real-option-sets)
7. [Transitional decisions](#7-transitional-decisions)
8. [Exceptions to standards](#8-exceptions-to-standards)
9. [Governance: which decisions get recorded, by whom](#9-governance-which-decisions-get-recorded-by-whom)

---

## 1. Finding the decisions worth deliberating

Scan the drivers and look for the choices that are **expensive to reverse**, **cross
component boundaries**, or **visible to stakeholders** (cost, compliance, hiring, vendor
lock-in). Typically three to six per project. Presenting fifteen decisions produces
decision fatigue and a rubber-stamp; presenting one hides the real trade-offs.

Rank them, because order matters. Some decisions constrain others — the deployment target
often constrains the persistence options, and the tenancy model constrains nearly
everything. Resolve upstream decisions first and say what they foreclose.

Some choices are not decisions at all: if there is one sane option given the constraints,
state it as a constraint-driven conclusion with a one-line rationale and move on. Do not
manufacture alternatives to fill a template.

---

## 2. Building an honest option set

**Two or three options per decision.** Four is usually two options plus two variations.

**Span the design space.** Options should differ in *kind*, not in degree — "Postgres
versus MySQL" is one option with a footnote; "relational store versus document store
versus event log" is a real choice. Each option should be one a competent engineer would
actually defend.

**Always include the simple option.** The boring monolith, the single database, the
managed service, the thing the team already runs. It is frequently correct, and if it is
not, showing why is the strongest possible argument for the more complex option.

**Describe each option in the same shape** so they can be compared:

- What it is, in two or three sentences
- How it satisfies the drivers, citing IDs (`NFR-004`, `US-021`)
- What it costs: money, complexity, operational load, new skills required
- What it forecloses or makes harder later
- When this option is the right answer

**Do not hide your view.** Present a recommendation with reasoning after laying out the
options fairly. A consultant who refuses to recommend is not being neutral, they are
transferring work to the person who asked. The point of the option set is to make the
recommendation *checkable* — the user can see the reasoning and disagree with a specific
step of it.

**State what would change your mind.** "I recommend the single service; if peak load turns
out to be above roughly 5k requests/second, or if two teams end up owning different parts
of this, the split becomes worth its cost." This turns a recommendation into a decision
rule that survives contact with new information.

---

## 3. Trade-off analysis

Score options against the drivers that came from the stories, not against generic virtues.
"Scalability: high" is meaningless; "handles the 2s p95 search requirement (NFR-004)
without a separate index" is checkable.

| Driver | Option A: single service | Option B: service split | Option C: serverless |
|---|---|---|---|
| 2s p95 search (NFR-004) | Met with a materialised view | Met, extra network hop | Met, cold starts risk p99 |
| 40 concurrent users (interview) | Comfortable | Over-provisioned | Comfortable |
| Team of 3, strong Django | Directly matches skills | Needs new ops skills | Needs new deploy model |
| Launch in 10 weeks | Fastest path | 3–4 weeks of extra setup | 2 weeks of learning |
| Under $400/month | ~$120 | ~$450 | ~$80, variable |
| **Reversibility** | Easy to split later if modular | Hard to rejoin | Hard to leave vendor |

Keep the **reversibility row** — often the deciding line when several options are
technically adequate. And be honest about the cost of your recommended option; an analysis
where the favourite wins every row reads as advocacy and invites reviewers to distrust it.

Beware the trade-off that is really a requirement in disguise. If an option fails a hard
compliance or budget constraint, it is not a lower-scoring option, it is disqualified —
say so plainly rather than letting it lose on points.

---

## 4. Reaching consensus

Documentation written before agreement is documentation that has to be rewritten. Get an
explicit decision on each significant choice first.

- **Confirm decision by decision**, not with one "does this all look OK?" at the end. Users
  agree to a wall of text far more readily than they agree to six specific things, and the
  first kind of agreement does not hold up.
- **Recommend defaults** so a user can accept the whole set quickly: "Unless you object, I
  will go with A, B, and D — the ones I'd most expect you to push back on are B and D."
- **Record dissent.** If the user chooses against your recommendation, write their choice
  as the decision and note the trade-off in the ADR's consequences. The record is more
  valuable when it is honest about what was given up.
- **Re-open the set if a new constraint appears.** A late "oh, it has to work offline" can
  invalidate earlier decisions; revisit them explicitly rather than patching around them.
- **Log deferred decisions** with what would trigger revisiting. "Not deciding" is a
  legitimate outcome as long as it is recorded and the option is kept open.

Only when the significant decisions are settled should the full document be written.

---

## 5. Writing the ADR

One decision per record, written in the past tense as of the day it was decided, and never
edited to pretend a different decision was made. When a decision changes, write a new ADR
and mark the old one superseded — the value of the log is the history, which is what stops
a future team from silently re-litigating a choice whose reasoning they cannot see.

Use `assets/adr-template.md`. The two sections that carry the weight:

- **Context** — the forces in play, with driver IDs. Written so a reader in two years who
  was not in the room can reconstruct why this was hard.
- **Consequences** — both directions. An ADR listing only benefits is marketing. Write the
  negative consequences plainly; they are what a future maintainer needs most, and they
  make the positive claims believable.

Also record the options rejected and *why* — one paragraph each is enough. Losing options
are what stop the same debate happening every six months.

Number ADRs sequentially (`ADR-001`), never renumber, and keep them next to the code they
govern so they are found when they are relevant.

---

## 6. Common decisions and their real option sets

Starting points, not answers. The right option depends entirely on the drivers.

| Decision | Options that genuinely differ | The pivot |
|---|---|---|
| System decomposition | Modular monolith · a few services around real boundaries · fine-grained services | Team count and independent deploy needs — not system size |
| Persistence | Relational · document · relational + search index · event log | Whether the data has invariants that need transactions |
| Sync vs async | Direct calls · queue/broker · event streaming | Whether the caller must wait, and tolerance for partial failure |
| API style | REST · GraphQL · RPC/gRPC · server-rendered, no API | Who consumes it and how varied their read needs are |
| Frontend | Server-rendered · SPA + API · hybrid (Next/Remix) · native | Interactivity, SEO, offline, and team skills |
| Identity | Managed IdP · existing SSO · self-hosted OSS · build it | Almost never build it; the pivot is which managed one |
| Hosting | Managed PaaS · containers on managed orchestration · serverless · VMs/on-prem | Ops capacity available, plus data residency |
| Multi-tenancy | Shared schema + tenant column · schema per tenant · database per tenant · instance per tenant | Isolation obligations and tenant count |
| Background work | In-process scheduler · queue + workers · managed workflow service | Whether work must survive a crash and be observable |
| Caching | None · in-process · shared cache · CDN/edge | Read amplification and staleness tolerance |
| Reporting | Query the operational DB · read replica · separate warehouse | Whether analytics queries would hurt production |

For each of these, the option to take seriously first is the least machinery that satisfies
the drivers. Complexity should be introduced deliberately, in response to a driver that is
written down — not inherited from a reference architecture built for a company at a
different scale.

---

## 7. Transitional decisions

Some decisions are correct now and wrong later by design: SQLite until there is a second
writer, a monolith until a second team owns a module, a manual process until volume justifies
automating it. Deferring cost until it is warranted is good engineering.

They go wrong in exactly one way — the expiry is never written down, and the temporary choice
becomes the permanent architecture by default. So a transitional decision is recorded as an
ADR with `state: transition` and must carry:

- **A trigger** that is observable: a threshold, an event, or a date. "When it becomes a
  problem" is not a trigger; "more than one writer process, database above 5 GB, or
  2027-06-01, whichever comes first" is.
- **A compatibility strategy** — what keeps the migration affordable (an abstraction, a
  restricted feature set, a documented export path), or an explicit acceptance that a rewrite
  is the plan.
- **The cost of deferral** — does waiting make the eventual migration harder, and by roughly
  how much? This is what makes the trade honest rather than optimistic.
- **An owner and a review date** in the frontmatter, so `scripts/check_freshness.py` surfaces
  it when the date passes.

Deferring a decision entirely is also legitimate — "not yet deciding the reporting store" —
and is recorded the same way, in the deferred table of the decision brief rather than as an
ADR. The difference: a transitional decision has been made and will be unmade; a deferred
decision has not been made yet. Both need triggers. See `references/evolution.md`.

## 8. Exceptions to standards

Where the design deviates from an organisational standard, the approved stack, or the paved
road, record it as an exception rather than letting it pass silently. An undocumented
deviation looks identical to an oversight, and the next reviewer has to relitigate it.

An exception record carries: the standard being deviated from, the reason, the cost of the
deviation (support, patching, expertise the platform team will not provide), who approved it,
and when it expires or is reconsidered. Time-boxing exceptions is what keeps the standard
meaningful — a permanent exception is either a bad standard or a bad exception, and both are
worth surfacing.

## 9. Governance: which decisions get recorded, by whom

**Record a decision when** it is expensive to reverse, crosses a team boundary, affects
security or compliance, commits money, or would surprise a future maintainer. Not every
technology choice needs an ADR; a decision nobody would question in a year does not need
defending in writing.

**Who decides** should be explicit and as local as possible: the team, for decisions inside
its own boundary; security consulted for anything touching authentication, data
classification, or external exposure; platform consulted where infrastructure cost or shared
capacity is involved. Everyone else is informed by the ADR itself.

**Time-box the review.** A decision review that cannot happen within a few days gets routed
around, and then the decision is made with no record at all — which is worse than a fast
review that occasionally approves something imperfect.

**Every decision gets a review trigger.** Not a rigid expiry, but a condition that should
prompt reconsideration: a load threshold, a contract renewal, a team change, a new regulatory
scope. Without one, decisions calcify simply because nobody remembers they were conditional.

**Link decisions to what implements and verifies them** — the requirements they serve, the
diagrams that show them, the components that realise them, the tests that prove them, and the
operational controls that enforce them. `scripts/check_coverage.py` and
`scripts/check_consistency.py` verify the parts of that chain that are mechanically
checkable; the rest is a review activity (`references/review-and-validation.md`).
