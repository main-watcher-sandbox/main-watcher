# Identity, security, and threat modelling

Security is the domain where retrofitting costs the most, because the expensive parts —
trust boundaries, tenant isolation, key custody, audit trails — are structural. They are
cheap while components are still being drawn and very expensive once data exists.

The goal here is not a security review checklist. It is to make the design's security
properties explicit enough that a reviewer can disagree with them.

## 1. Identity: authenticate before authorising

Establish, for each kind of principal, how identity is proved:

| Principal | Mechanism | Notes |
|---|---|---|
| Human users | OIDC against the existing IdP | Auth code + PKCE for public clients |
| Service-to-service | Workload identity / mTLS | Avoid shared static API keys |
| Devices | Certificate or device code | Enrolment and revocation path matters |
| Administrators | Same IdP, separate role, MFA enforced | Break-glass path documented and audited |

**Do not build authentication.** The realistic decision is which managed provider, and how
the session and token model works. Password storage, MFA, account recovery, and enumeration
resistance are solved problems that are easy to get subtly and catastrophically wrong.

Decide and record: token lifetime, refresh strategy, revocation (what happens within how
long when someone is disabled), session fixation and logout behaviour across devices.

## 2. Authorisation: pick a model and state where it is enforced

- **RBAC** — roles map to permissions. Sufficient for most internal systems.
- **ABAC / policy** — decisions depend on attributes (department, ownership, time). More
  expressive, harder to reason about and to test.
- **Relationship-based** — "the supervisor of the submitter can approve". Very common in
  practice, and usually implemented badly as scattered `if` statements.

Write the **authorisation matrix** — roles down, resources and operations across, cells
showing scope (own / team / all / none). The matrix belongs in the architecture document,
because it is a design artifact QA and security both test against.

Enforce authorisation **server-side, at one identifiable layer**. A UI that hides a button is
not access control. If the decision is spread across controllers, repositories and views,
nobody can answer "who can see this record?" without reading everything.

## 3. Multi-tenancy and isolation

If the system serves multiple tenants, isolation strength is an architectural decision with
compliance and cost consequences:

| Model | Isolation | Cost / complexity | Typical fit |
|---|---|---|---|
| Shared schema, tenant column | Logical, enforced in code | Lowest | Many small tenants, low regulatory pressure |
| Schema per tenant | Logical, enforced by the database | Moderate | Tens to hundreds of tenants |
| Database per tenant | Strong | Higher ops burden | Regulated data, contractual isolation |
| Instance per tenant | Strongest | Highest | Few large tenants paying for it |

With a shared schema, the tenant filter must be structurally impossible to forget — row-level
security, a repository layer that requires tenant context, or session-scoped enforcement.
"Every query includes the tenant ID" as a coding convention will eventually fail, and the
failure mode is one tenant seeing another's data.

## 4. Secrets, keys, certificates

Name where each lives and how it rotates: platform secret store with workload identity is
the default; environment variables holding long-lived credentials are not. For encryption,
say who holds the keys (provider-managed, customer-managed, customer-supplied), because that
answer is frequently a contractual requirement rather than a technical preference. Include
certificate expiry and renewal in the operational design — expired certificates remain a
leading cause of self-inflicted outages.

## 5. Threat modelling

Do it once the container view exists and before the build commits. The method matters less
than doing it at all; STRIDE per boundary is a reliable default.

**Step 1 — Draw the trust boundaries.** Anywhere the level of trust changes: internet to
edge, edge to application, application to data, tenant to tenant, staff to production. Use a
data-flow diagram (see the diagram catalog) rather than a component diagram — threats follow
data, not deployment units.

**Step 2 — For each flow crossing a boundary, ask STRIDE:**

| Threat | Question | Usual mitigation |
|---|---|---|
| **S**poofing | Can something pretend to be this principal? | Strong authentication, mTLS, signed tokens |
| **T**ampering | Can data be altered in flight or at rest? | TLS, integrity checks, append-only audit |
| **R**epudiation | Can someone deny doing it? | Audit log with actor, time, immutable |
| **I**nformation disclosure | Can data leak to the wrong party? | Authorisation, encryption, scoped URLs, minimisation |
| **D**enial of service | Can it be exhausted or made unavailable? | Rate limits, quotas, timeouts, isolation |
| **E**levation of privilege | Can a principal gain rights it should not have? | Least privilege, server-side checks, no client-supplied roles |

**Step 3 — Record each threat with a decision**: mitigated (how, and where it is tested),
accepted (by whom, with an expiry), or transferred. A threat model with no accepted risks is
usually a threat model nobody thought about seriously.

**Step 4 — Include abuse cases, not just attacks.** Insider misuse, a support agent browsing
records out of curiosity, automated scraping, a customer abusing a free tier. These rarely
appear in attack-centric models and are frequently the ones that actually happen.

Use `assets/threat-model-template.md`.

## 6. Auditing and monitoring

Decide which events are security-relevant — authentication outcomes, authorisation denials,
privilege changes, data exports, administrative actions, access to sensitive records — and
record for each: what is captured, where it goes, how long it is retained, and **who reviews
it**. An audit log nobody reads only helps after an incident; an alert on anomalous access
helps during one.

Audit records are usually append-only by requirement. That is a data-model decision (see
`references/data-and-domain.md`), not a logging configuration.

## 7. Compliance mapping

Where a named regime applies, map obligations to design elements so an auditor can follow
the thread — and so the team can tell which controls are load-bearing.

| Obligation | Regime | Design element | Verified by |
|---|---|---|---|
| Right to erasure | GDPR Art. 17 | Deletion job + backup expiry policy | Test case TC-114 |
| Cardholder data not stored | PCI-DSS | Tokenisation via gateway | Design review + scan |

## 8. Supply chain and vulnerability management

Dependency scanning in CI, a patching expectation (how fast for critical, who decides), image
provenance and signing, and a policy for generated or vendored code. Also name the incident
response path: who is called, what the disclosure obligations are, and what the rollback
options are — decided in advance, because these are terrible decisions to make at speed.

## 9. Artifacts this domain produces

Trust-boundary diagram, identity/authentication flow (usually a sequence diagram),
authorisation matrix, data classification table, threat model with accepted risks, compliance
mapping, and ADRs for the identity provider, the tenancy model, and key custody.
