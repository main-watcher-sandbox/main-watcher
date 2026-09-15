# Integration and dependency architecture

Integrations cause a disproportionate share of production incidents and schedule slips,
because their failure modes sit outside your control and are usually undocumented. The
design question is never just "how do we call it" — it is "what do we do when it is slow,
wrong, or gone, and for how long can we tolerate that?"

## 1. Inventory every dependency

An inventory is worth writing down even for two dependencies, because it forces the
ownership and failure questions that otherwise get discovered in production.

| Dependency | Owner | Criticality | Interaction | SLA (stated / observed) | On failure |
|---|---|---|---|---|---|
| Stripe | Vendor | Critical — no checkout without it | Sync REST | 99.99% stated | Queue order, retry, notify user |
| Ceridian payroll | Internal finance | Critical — daily | Nightly batch | None stated | Retry next night, alert payroll |
| SendGrid | Vendor | Degraded — email delayed | Async | 99.9% stated | Queue, retry with backoff |

**Criticality means what happens without it**, not how important the vendor is. Classify as:
*critical* (the system cannot perform its purpose), *degraded* (some capability lost),
*cosmetic* (users may not notice). This ranking decides where resilience effort goes.

Note the difference between a **stated** and an **observed** SLA. An internal system with no
written SLA and a history of Friday-afternoon outages is a higher risk than a vendor at
99.9%, and pretending otherwise designs for a fiction.

## 2. Contracts and versioning

For each interface, record: protocol, format, schema location, authentication, versioning
policy, and who may change it. The critical distinction is whether **you** or **they** control
the contract, because it decides who absorbs breaking changes.

- **You own the interface** — publish a versioned contract, support at least one previous
  version, and deprecate on an announced schedule. Consumers you cannot enumerate mean you
  cannot make breaking changes at all; find out which case you are in before designing.
- **They own it** — pin the version, isolate it behind an adapter so their model does not
  leak into your domain, and subscribe to their change notifications. The adapter is what
  makes their breaking change a local edit rather than a refactor.

Prefer additive change: new optional fields, new endpoints, tolerant readers that ignore
unknown fields. Most "breaking" changes are only breaking because a consumer validated more
strictly than it needed to.

## 3. Failure handling — decide it per integration

Defaults that apply almost everywhere:

- **Timeouts on every call.** No timeout means inheriting the slowest possible behaviour of
  someone else's system, and thread exhaustion is how one slow dependency takes down an
  unrelated feature.
- **Retries only for idempotent operations**, with exponential backoff and jitter. Retrying a
  non-idempotent payment is how duplicate charges happen; retrying in lockstep is how a
  recovering dependency gets knocked over again.
- **Idempotency keys** on anything that creates or moves money or state.
- **Circuit breaker** on critical synchronous dependencies, so a sustained failure fails fast
  rather than queueing every request thread behind it.
- **Bulkheads** — separate connection pools or workers per dependency, so one saturating
  integration cannot exhaust the resources others need.
- **Dead-letter handling** for async: what happens to a message that fails repeatedly, who
  sees it, and how it is replayed.

State the **user-visible behaviour** for each failure, because that is a product decision
wearing a technical costume: does the user see an error, a delay, a queued confirmation, or
stale data? Get it agreed rather than defaulting to a 500.

## 4. Synchronous or asynchronous

| Choose sync when | Choose async when |
|---|---|
| The caller genuinely cannot proceed without the answer | The work can complete later without blocking the user |
| The operation is fast and the dependency is reliable | The dependency is slow, rate-limited, or flaky |
| Simplicity matters more than isolation | Failure isolation and throughput smoothing matter |

Async is not free: it introduces delivery semantics, ordering questions, duplicate handling,
and a whole class of "where did my message go" operational work. Introduce a broker when a
driver requires it — not because it looks more modern.

If async: state delivery semantics (at-least-once is the realistic default, which means
consumers must be idempotent), whether ordering matters and how it is preserved, and how
poison messages are handled.

## 5. Rate limits, quotas, and cost per call

Record the limits before designing the flow, since they frequently invalidate the obvious
design — a nightly batch of 50,000 records against a 10 requests/second limit takes 83
minutes, which may not fit the window. Where the vendor charges per call, the integration
pattern becomes a cost decision too: caching, batching, or webhook-driven updates instead of
polling.

## 6. Vendor risk and exit

For each critical vendor, record: contractual commitments, data residency, what happens to
your data on termination, and — realistically — what switching would cost. Full portability
is usually not worth designing for, but an adapter boundary and an export path usually are.

Be honest in the ADR about lock-in accepted deliberately. "We accept lock-in to this managed
service because operating it ourselves would cost more than switching ever will" is a fine
decision; discovering the lock-in later is not.

## 7. Testing integrations

Contract tests against a published schema catch drift earlier and cheaper than end-to-end
tests. Also plan for: a sandbox or simulator for development, recorded fixtures for CI, and a
periodic real-dependency smoke test — because sandboxes drift from production behaviour, and
the drift is discovered during incidents. See `references/quality-and-testing.md`.

## 8. Artifacts this domain produces

Integration context diagram, dependency inventory with criticality and failure behaviour, API
and event catalogue where interfaces are consumed externally, contract specifications,
failure-mode analysis for critical dependencies, vendor risk notes, and ADRs for each
significant integration pattern choice.
