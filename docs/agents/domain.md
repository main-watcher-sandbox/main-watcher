# Domain Docs

How the engineering skills should read this repo's domain documentation before exploring it.

## Read these first

- **`CONTEXT.md`** at the repo root, if it exists.
- **`docs/architecture/architecture.md`** (ARCH-001), the main architecture document.
- **`docs/architecture/decisions/`**: read the ADRs that touch the area you're about to work in.
  Skip ADR-005 and ADR-006; they are superseded by ADR-007 and ADR-009. Check the note at the
  top of each ADR for later ADRs that amend it.

If `CONTEXT.md` doesn't exist, **carry on without mentioning it**. The `/domain-modeling` skill
creates it once a term actually gets settled.

## File structure

One set of domain docs for the whole repo:

```
/
├── CONTEXT.md
└── docs/architecture/
    ├── architecture.md
    ├── test-strategy.md
    └── decisions/
        ├── ADR-001-central-watcher.md
        └── ADR-017-retry-neutral-results.md
```

## ADR conventions (this repo)

- ADRs are numbered `ADR-NNN-short-slug.md` in `docs/architecture/decisions/`. Don't use
  `docs/adr/` or `0001-` numbering.
- **Accepted ADRs are never edited.** A changed decision gets a new ADR that supersedes or
  amends the old one, plus a note at the top of the old one.
- After editing docs, run the validation commands listed in `CLAUDE.md`.

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a proposal, a test name), use the
term as it is defined in `CONTEXT.md` and ARCH-001, for example lock issue, gate, Planner,
Reporter, lease. Don't switch to synonyms.

If a concept isn't in the glossary, either you're inventing language the project doesn't use
(reconsider) or the glossary is missing it (note that for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an ADR, say so explicitly rather than quietly overriding it:

> _Contradicts ADR-002 (merge-queue gate), but worth reopening because…_
