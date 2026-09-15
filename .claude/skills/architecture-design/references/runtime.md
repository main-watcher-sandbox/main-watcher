# Runtime: deployment, performance, resilience, operations

These four concerns are separated in most frameworks but decided together in practice: where
software runs determines how it scales, how it fails, and how anyone finds out.

## Contents

1. [Deployment and release](#1-deployment-and-release) · 2. [Self-updating software](#2-self-updating-software) ·
3. [Environments and configuration](#3-environments-and-configuration) · 4. [Performance and capacity](#4-performance-and-capacity) ·
5. [Resilience](#5-resilience) · 6. [Observability](#6-observability) · 7. [Operations and support](#7-operations-and-support)

---

## 1. Deployment and release

Record where each component runs, how it gets there, and how a bad release is undone. The
last is the one people skip and the one that matters at 2am.

**Deployment strategy** — pick per component, based on what a bad release costs:

| Strategy | Gives you | Costs |
|---|---|---|
| Rolling | Simple, no extra capacity | Mixed versions live simultaneously |
| Blue-green | Instant rollback, clean cutover | Double infrastructure during release |
| Canary | Early detection on real traffic | Needs routing control and good metrics |
| Feature flags | Decouples deploy from release | Flag debt, combinatorial test surface |

Mixed versions during rollout are not an edge case, they are the normal state — so
**every release must be backward compatible with the one before it**, in API and in data.

**Database changes** are the hard part of release design. Expand-and-contract keeps schema
and code independently deployable: add the new column, deploy code writing both, backfill,
switch reads, then drop the old column in a later release. A migration that requires
application downtime is a design decision needing agreement, not a deployment detail.

**Rollback** — say what it means for each component. Stateless services roll back cleanly;
a completed migration usually cannot, so the rollback plan there is roll-forward with a fix,
and that has to be understood before the release, not during it.

## 2. Self-updating software

If the application updates itself — desktop apps, agents, edge devices, embedded clients —
the update mechanism is a critical component with its own failure modes, and a bad update
can break every install simultaneously.

Decide and record:

- **Signing and provenance** — updates signed, signature verified before install, key custody named.
- **Staged rollout** — percentage-based, with a pause and a halt control that works even
  when the fleet is misbehaving.
- **Health signal** — how the system knows an update is bad, and how fast.
- **Rollback or roll-forward** — can a client revert, or does recovery require another update?
  If the latter, the update path itself must never be the thing that breaks.
- **Version skew** — how far behind may a client be, and what does the server do with a
  client several versions old? Skew tolerance is a compatibility contract, not an accident.
- **Offline and blocked clients** — what happens to a device that misses updates for months.

## 3. Environments and configuration

Name the environments and what each is for. State how production-like each is, and where it
deliberately differs — an undocumented difference between staging and production is where
"it worked in staging" comes from.

Infrastructure and configuration should be code, in version control, applied by pipeline.
Configuration is supplied at runtime by the platform; secrets come from a secret store via
workload identity. If production data is copied into a lower environment, that is a data
protection decision requiring masking and an explicit approval, not a convenience.

## 4. Performance and capacity

Start from the numbers, not from optimisation. Write the workload model down:

| Metric | Expected | Peak | 24-month growth |
|---|---|---|---|
| Concurrent users | 40 | 120 | +20% |
| Claims/day | 200 | 600 (month end) | +50% |
| p95 submit latency | — | target 3s | unchanged |

Then a **performance budget** for the critical paths: how the target is spent across network,
application, database and third parties. A budget makes the bottleneck predictable and gives
a specific thing to test, instead of a vague hope that it will be fast enough.

Identify the likely bottleneck and how it would be observed. Common ones: database
connections, N+1 queries, a single-threaded step, a third-party rate limit, unbounded result
sets, and object storage egress.

**Scaling approach** — vertical first (simpler, and often sufficient), horizontal where the
component is stateless, partitioned where data volume drives it. Record the ceiling of each
approach, because knowing that vertical scaling stops working at roughly 8× today's load is
what tells you when to revisit.

Techniques worth naming explicitly when used: caching (with staleness tolerance and
invalidation), batching, read replicas, queueing to smooth spikes, backpressure so a queue
cannot grow without bound, and load shedding to protect the system's core function under
overload. Each adds a failure mode — a stale cache serves wrong data, a queue hides an
outage — so each should trace to a driver.

**Validate before launch.** A load test against the stated target, run in a production-like
environment, is what converts an assumption into a fact. Until then the numbers are labelled
`[assumption]`.

## 5. Resilience

Enumerate the failures you expect — instance loss, availability-zone loss, dependency
outage, network partition, data corruption, poison message, traffic spike, expired
certificate — and for each state the detection, the response, and the user-visible effect.

| Failure | Detection | Response | User sees |
|---|---|---|---|
| Payment gateway down | Circuit breaker opens | Queue order, retry | "Payment processing, we'll confirm shortly" |
| AZ loss | Health checks | Traffic to healthy AZ | Brief errors, then normal |
| Database primary loss | Managed failover | Automatic, ~60s | Errors for ~60s |

**Graceful degradation** beats binary availability: state which capabilities must keep
working when a dependency is gone. Read-only mode, cached content, or queued writes usually
preserve most of the value.

Set **availability targets, RTO and RPO** as numbers, and check them against the design.
An RPO of zero requires synchronous replication, which has a latency cost; an RTO of minutes
requires tested automation, not a runbook someone follows manually. Targets nobody costed
tend to be aspirational, and the gap surfaces during the first real incident.

Guard against the self-inflicted failures: retry storms (backoff and jitter), cascading
failure (circuit breakers, bulkheads), duplicate processing (idempotency), and thundering
herds on cache expiry (staggered TTLs).

**Test the resilience design.** An untested failover is a hypothesis. A restore that has
never been performed is not a backup strategy.

## 6. Observability

Design what the system emits, because a design that cannot be diagnosed at 3am is defective
regardless of how well it performs.

- **Logs** — structured, with correlation ID, actor, and outcome. No secrets, no personal
  data beyond what is necessary and classified.
- **Metrics** — the four that matter for a request-serving system are rate, errors, duration
  and saturation; for a queue-based system, add depth and age of the oldest message.
- **Traces** — propagate a correlation ID across every component and into third-party calls
  where possible. Retrofitting propagation is far more expensive than designing it in.
- **Business signals** — claims submitted, exports completed. These catch the failures where
  every technical metric looks healthy and nothing is actually happening.
- **Audit records** — see `references/security.md`; separate retention and access rules.

**SLIs and SLOs**: pick a small number of indicators that reflect user experience (not CPU),
set objectives someone has agreed, and alert on the objective being at risk rather than on
every anomaly. Alerts that are not actionable train people to ignore alerts, which is worse
than having none.

| SLI | SLO | Alert on |
|---|---|---|
| Successful claim submission rate | 99.5% over 30 days | Error budget burn rate |
| Nightly export completed by 04:00 | 99% of nights | Not completed by 03:30 |

## 7. Operations and support

- **Ownership** — who is on call, in what hours, for which components. Unowned components in
  production are an outage waiting for an owner.
- **Runbooks** — one per alert, saying what it means, how to confirm, what to do, and when to
  escalate. Written when the alert is created, not after it first fires.
- **Health checks** — liveness versus readiness, and what each actually checks. A health check
  that only proves the process is running will report healthy during a total outage.
- **Production access** — who has it, how it is granted, whether it is time-bound, and how it
  is audited. Break-glass access should be possible, alarming, and logged.
- **Automation** — automate the repetitive and reversible; keep destructive operations manual,
  with confirmation.
- **Supportability test** — before release, take a plausible fault and confirm someone could
  diagnose it using only what the system emits. This finds missing observability while it is
  still cheap to add.

## 8. Artifacts this domain produces

Deployment diagram, environment topology, network diagram, CI/CD flow, release and rollback
strategy, workload model, performance budget, capacity plan, failure-mode table, SLO
definitions, alert catalogue, runbook index, and operational ownership matrix. Select against
risk — see `references/artifact-selection.md`.
