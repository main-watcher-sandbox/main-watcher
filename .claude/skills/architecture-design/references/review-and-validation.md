# Review, validation, and freshness

Scripts catch mechanical defects: broken diagrams, dangling IDs, missing owners, overdue
reviews. They cannot tell whether a design will work. This reference covers the judgement
part — and the contradiction patterns worth looking for specifically, because they are how
architecture documents go quietly wrong.

Run this before handing anything over, and **report what you found rather than silently
fixing it**. A user who does not know where the design is thin cannot compensate for it.

## 1. What the scripts cover

```bash
python3 scripts/render_diagrams.py docs/architecture/ --check   # diagram syntax
python3 scripts/check_coverage.py --stories backlog/ --arch docs/architecture/
python3 scripts/check_consistency.py docs/architecture/          # naming, refs, ADR completeness
python3 scripts/check_consistency.py docs/architecture/ --json report.json --strict  # CI gate
python3 scripts/check_freshness.py docs/architecture/            # owners, dates, unconfirmed items
```

| Script | Catches | Does not catch |
|---|---|---|
| `render_diagrams --check` | Invalid Mermaid | Unreadable layout, wrong content |
| `check_coverage` | Requirements nobody designed for, dangling IDs | Whether the design actually satisfies them |
| `check_consistency` | Name drift, unresolved ADR refs, superseded decisions still cited, tech contradictions, ADRs missing Context/Decision/Consequences/Revisit, one-option decisions, transitional decisions with no trigger | Semantic contradictions in prose |
| `check_freshness` | Missing owners, overdue reviews, expired transitional decisions, outstanding `[unconfirmed]` items | Whether the document matches the code |

## 2. The review checklist

Work through these six dimensions. For each finding, record severity, the driver or risk it
threatens, and a suggested action — not a preference.

### Completeness
- Does every driver from step 1 have a design response, or an explicit statement that it is unmet?
- Does every component trace to a requirement?
- Are the in-play domains from the risk screen each addressed or explicitly excluded?
- Are there decisions being made implicitly that should be ADRs — usually visible as a
  technology named in a diagram with no decision behind it?

### Consistency
- Same component name everywhere: diagrams, prose, ADRs, code?
- Do the diagrams agree with each other? A container in the deployment view but not the
  container view means one of them is stale.
- Do ADRs contradict each other, or contradict the main document?
- Is any superseded decision still being cited as current?

### Feasibility
- Can this team build this in the time available with the skills they have?
- Does the design assume infrastructure, budget, or specialists that exist?
- Are the quality attribute targets achievable with the chosen mechanisms, or merely asserted?
- Does the architecture imply a team structure that does not exist?

### Security
- Is every trust boundary identified, and does data crossing it have a stated control?
- Is authorisation enforced server-side at an identifiable layer?
- Are secrets and keys handled by a store rather than configuration?
- Do the accepted risks have named acceptors and expiry dates?

### Operability
- Could someone diagnose a plausible fault at 3am using only what the system emits?
- Does every alert have a runbook and an owner?
- Is every component owned by someone who knows they own it?
- Is there a rollback story for each deployable, including data changes?

### Testability
- Does each quality attribute claim have a test that would fail if it were false?
- Can components be tested in isolation, or does everything require a full environment?
- Is test data realistic and lawfully obtained?
- Are the resilience mechanisms (failover, restore, retry) actually exercised?

## 3. Contradiction patterns worth hunting

These recur across projects and are rarely caught by reading top to bottom, because each half
of the contradiction reads fine on its own:

| Pattern | Looks like | Why it matters |
|---|---|---|
| **Diagram/prose drift** | Prose describes a cache; no cache in any diagram | One of them is what gets built |
| **Stale technology** | ADR chose PostgreSQL, deployment view shows MySQL | Someone changed their mind without an ADR |
| **Orphan component** | A box in a diagram appearing nowhere else | Either speculative or a documentation gap |
| **Unbacked NFR** | "99.9% availability" with a single-instance deployment | The target is decorative |
| **Contradictory consistency** | "Eventual consistency" plus a stated read-your-writes requirement | Someone will discover this in testing |
| **Retention conflict** | 7-year retention plus a 30-day backup policy and no archive | The obligation is not actually met |
| **Sync/async mismatch** | Sequence shows a synchronous call; the container view shows a queue | Different failure semantics entirely |
| **Phantom SLA** | Resilience design assumes a vendor SLA that does not exist contractually | The plan depends on hope |
| **Untriggered transition** | "Temporary" with no trigger or review date | Becomes permanent by default |
| **Ownerless artifact** | No owner in frontmatter | Nobody will update it |
| **Superseded but cited** | Current doc references an ADR marked superseded | The reader follows a dead decision |

`scripts/check_consistency.py` detects several of these mechanically — name drift, dangling
and superseded references, technology contradictions where the same component is given
different technologies in different files, and ADRs that are structurally incomplete
(missing Context, Decision, Consequences or a revisit trigger; no negative consequences
recorded; only one option considered). The rest need reading.

For a CI gate, `--json report.json` writes a machine-readable report alongside the console
output, and `--strict` makes advisory warnings fail the run. Start without `--strict`: name
drift heuristics produce occasional false positives, and a gate that cries wolf gets
disabled rather than fixed.

## 4. Detecting drift from the implementation

Documentation contradicted by the code is worse than no documentation, because people act on
it. Where the code is available, spot-check the load-bearing claims rather than trying to
verify everything:

- Do the components in the container view correspond to things that actually exist — services,
  projects, deployables?
- Does the dependency list match the manifest files?
- Do the datastores in the design match the connection strings and migrations present?
- Do the endpoints in a sequence diagram exist?
- Are the claimed security controls (authorisation middleware, encryption settings,
  rate limits) actually present?

Report drift as findings against the document, and prefer updating the document to match
reality — the code is the truth. Where the code is wrong instead, that is a defect, and it
belongs in the risk register with an owner.

Timestamps help: `check_freshness.py` compares `reviewed:` against file modification dates
and flags artifacts whose subject has changed more recently than their last review.

## 5. Writing the review

Produce a review artifact (`type: review` in frontmatter) rather than a chat message when the
review has more than a few findings, so it can be tracked and revisited.

| ID | Severity | Finding | Threatens | Suggested action | Owner |
|---|---|---|---|---|---|
| F-01 | High | No stated behaviour when the payment gateway is unavailable | NFR-002, revenue | Add failure path to the checkout sequence; ADR on queue-and-retry | Claims lead |
| F-02 | Medium | Container view and deployment view disagree on the worker count | Operability | Reconcile; deployment view appears current | Author |
| F-03 | Low | Glossary missing three domain terms used in ADR-004 | Comprehension | Add terms | Author |

Rank by severity, and be explicit about what you did **not** review — an unreviewed area
that looks reviewed is the most misleading output this process can produce.

Severity guidance: **High** — would cause an outage, a breach, a compliance failure, or an
unbuildable design. **Medium** — will cause avoidable rework or operational pain. **Low** —
clarity and maintainability. Resist inflating severity to force attention; a review where
everything is high is a review nobody triages.
