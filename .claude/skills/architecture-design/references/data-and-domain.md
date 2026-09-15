# Domain and data architecture

Data outlives everything else in the design. Application code gets rewritten routinely;
the data model, its history, and the rules that were enforced on the way in are what any
replacement system inherits. Treat data decisions as the ones with the longest half-life.

## 1. Model the domain before choosing the store

Extract the concepts from the stories: repeated nouns are candidate entities, and clauses
like "cannot be cancelled once shipped" are invariants. Group them into **bounded contexts**
— areas where a word means one thing. The moment "customer" means something different to
billing than to support, that is a context boundary, and forcing one shared model across it
produces a schema nobody can change.

Identify the **aggregate boundaries**: the clusters that must change together and be
consistent at every instant. This is the most useful thing a domain model gives you, because
aggregate boundaries are transaction boundaries, and transaction boundaries constrain how
the system can be split later.

Record for each entity: who owns it, its identifier, its lifecycle, and which invariants
must always hold.

## 2. Ownership and authority

For every category of data, name exactly one **system of record**. Everything else is
derived, cached or replicated — label it as such.

| Data | System of record | Derived / cached copies | Staleness tolerated |
|---|---|---|---|
| Employee identity | Entra ID | Local `employee` table, synced nightly | 24h |
| Claim | This system | Payroll export | Until next batch |

Ambiguous ownership is the root cause of most data-integrity incidents: two systems both
believe they can edit a value, and reconciliation becomes a permanent manual job. If two
systems genuinely must write the same data, that is a decision requiring an ADR and a stated
conflict-resolution rule, not an implementation detail.

## 3. Consistency, transactions, idempotency

Ask what each operation actually needs, rather than defaulting:

- **Strong/transactional** — invariants that must never be violated, typically inside one aggregate.
- **Read-your-writes** — the user must see their own change immediately; others can lag.
- **Eventual** — acceptable if the system converges and the user is not misled meanwhile.

Where an operation spans components, there is no free distributed transaction. The realistic
options are: keep it in one transaction by not splitting the components; use an outbox so
the state change and the event publish are atomic locally; or accept a saga with explicit
compensation. Say which, and say what a partial failure leaves behind.

**Idempotency is a data-model property.** Retries, at-least-once delivery, and duplicate user
submissions are inevitable, so every externally triggered write needs a natural or supplied
key that makes a repeat harmless. Decide where that key comes from — client-generated ID,
request hash, provider reference — and record it, because retrofitting idempotency after
duplicates appear in production is painful and manual.

**Concurrency:** decide optimistic (version column, reject on conflict) versus pessimistic
(locking) per aggregate, and say what the user sees when a conflict occurs.

## 4. Choosing persistence

Start from the shape of the data and its access patterns, not from familiarity or fashion.

| Signal | Points toward |
|---|---|
| Relations, invariants, ad-hoc queries, reporting | Relational |
| Self-contained documents, flexible schema, few cross-entity rules | Document store |
| Immutable history is the truth; state is a projection | Event log + read models |
| Full-text or faceted search with latency targets | Relational + search index |
| Simple key lookups at high volume | Key-value / cache |
| Time-ordered metrics at volume | Time-series |

A relational database with JSON columns covers a surprising amount of ground and stays one
decision, which is why it is the right default until a driver says otherwise. Polyglot
persistence multiplies operational burden — backups, migrations, monitoring and expertise per
store — so each additional store should trace to a written driver.

## 5. Classification, residency, retention

Classify every data category (public, internal, personal, sensitive personal, regulated) and
attach the obligations that follow. The classification drives encryption, access control,
logging, backup handling and test-data policy, so it is genuinely architectural rather than
paperwork.

| Category | Example | Residency | Retention | Deletion | Legal hold |
|---|---|---|---|---|---|
| Sensitive personal | Receipt images | Canada only | 7 years | On request, subject to hold | Suspends deletion |

Retention needs a mechanism, not just a policy: a lifecycle rule, a scheduled job, a
partition drop. And erasure obligations must be checked against backups and derived copies —
"delete on request" is a lie if the row survives in a warehouse, an export, and six months of
backups. Say explicitly how far erasure reaches.

## 6. Schema evolution and migration

Once production data exists, every schema change is a deployment problem. Standardise on
expand-and-contract: add the new shape, write to both, backfill, switch reads, then remove
the old — each step independently deployable and reversible. Decide how migrations are
coordinated with releases (see `references/runtime.md`) and whether the application must
tolerate both shapes during rollout.

Backup and restore deserve a real answer, not "the platform does it": how often, retained how
long, encrypted with which key, and — the part usually skipped — **when was a restore last
tested?** An untested backup is a hypothesis, and the discovery that it does not work always
happens on the worst possible day.

## 7. Transitional storage decisions

Starting on SQLite, a single instance, or a flat-file store is often correct — cheap, fast,
and adequate for the load actually present. It becomes a problem only when the temporary
nature is not written down.

A transitional storage decision must carry:

- **The trigger** — concurrent writers above N, data above X GB, a second instance, a
  reporting need that competes with production, or a specific date.
- **The compatibility strategy** — an abstraction layer, standard SQL only, a documented
  export path, or an accepted rewrite.
- **The migration outline** — how data moves, whether downtime is required, how it is verified.
- **An owner and a review date** in the artifact frontmatter.

Record it as an ADR with `state: transition`, and expect `scripts/check_freshness.py` to
raise it when the review date passes. That is the mechanism that stops "we'll move to
PostgreSQL later" from silently becoming the permanent architecture.

## 8. Artifacts this domain produces

Conceptual model (bounded contexts and their relationships), logical data model (ERD), data
ownership matrix, classification and retention table, data-flow diagram where sensitive data
crosses boundaries, and ADRs for the store choice, the consistency model, and any
transitional decision. See `references/diagram-catalog.md` for the notation.
