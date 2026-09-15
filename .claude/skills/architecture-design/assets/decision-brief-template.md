# Architecture options — [System name]

> Use this to present options **before** writing the architecture document. The goal is a
> decision on each item, not a document. Keep it short enough to read in one sitting —
> if it runs past a couple of pages, there are too many decisions in play and the
> less consequential ones should be settled as implementation choices instead.

## What I understood from the stories

Three or four lines summarising the system, so a wrong assumption surfaces now rather than
after the design is written.

**Drivers I am designing against**

| Driver | Source |
|---|---|
| | NFR-004 |

**Assumptions I have made** — correct any of these and I will rework the affected options.

- …

---

## Decision 1 — [The question, phrased as a choice]

*Why this matters:* one line on what it affects and how hard it is to undo later.

### Option A — [name]

- **What it is:** …
- **Fits the drivers because:** … (cite IDs)
- **Costs:** money, complexity, operational load, skills needed
- **Forecloses:** …

### Option B — [name]

### Option C — [name]

### Comparison

| Driver | A | B | C |
|---|---|---|---|
| | | | |
| **Reversibility** | | | |

### Recommendation

**[Option X]**, because […]. I would change this recommendation if […].

**Your call:** A, B, C, or something I have not considered?

---

## Decision 2 — [The question]

…

---

## Deferred

Decisions deliberately not being made now, with what would trigger making them.

| Decision | Deferred because | Revisit when |
|---|---|---|

## Still need from you

Open questions that materially affect the design, each with the default assumed if there is
no answer — so silence still produces a working design rather than a stall.

| Question | Default if unanswered |
|---|---|
