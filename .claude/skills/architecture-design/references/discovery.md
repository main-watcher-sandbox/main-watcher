# Discovery: what to ask, and what not to

User stories describe behaviour. Architecture is mostly determined by everything the
stories leave out — load, data sensitivity, team shape, deployment target, budget, and
what already exists. The interview exists to recover that missing context.

The discipline that makes an interview good is **asking only the questions whose answers
change a decision**. Every question spends the user's patience, and a list of forty
questions gets abandoned or answered carelessly, which is worse than not asking.

## Contents

1. [How to run the interview](#1-how-to-run-the-interview)
2. [Reading drivers out of the stories first](#2-reading-drivers-out-of-the-stories-first)
3. [The question bank](#3-the-question-bank)
4. [Questions not worth asking](#4-questions-not-worth-asking)
5. [When the user does not know](#5-when-the-user-does-not-know)

---

## 1. How to run the interview

**Do the homework first.** Read the stories, extract what they already tell you, and open
with what you inferred rather than with questions. "Your stories imply three user roles,
a payments integration, and a hard 2-second search requirement — I've assumed a
single-region web app for now. Correct me where I'm wrong" gets a better response than a
blank questionnaire, because reacting is easier than generating.

**Batch by theme, one round at a time.** Five to eight questions per round, grouped so the
user stays in one mental context (all the data questions together, all the ops questions
together). Two or three rounds is usually the whole interview.

**Offer candidate answers, not open prompts.** "How many concurrent users at peak — under
100, hundreds, or tens of thousands?" is answerable in a second. "What are your scalability
requirements?" makes the user do your job. Attach a recommended default to each question so
a busy user can reply "defaults are fine" and still get a competent design.

**Say why you are asking.** "Asking because if any of this data is health or payment data,
it changes the storage and audit design significantly." Users answer better questions when
they can see the consequence, and often volunteer the thing you did not know to ask.

**Never block on an answer.** Where an answer is missing, choose the reasonable default,
label it clearly as an assumption, and carry on. A visible assumption gets corrected in
review; an unanswered question stalls the work.

---

## 2. Reading drivers out of the stories first

Before asking anything, mine the backlog. Most of the architecturally significant material
is already there, especially if the stories came with derived requirements.

- **Non-functional requirements are the architecture.** NFRs about latency, throughput,
  availability, retention, and compliance constrain the design far more than any feature
  does. Pull each one into a driver table with its ID.
- **Nouns become domain concepts.** Repeated nouns across stories are candidate entities
  and aggregate boundaries.
- **Roles become actors and a permission model.** Distinct roles in the "As a…" clauses
  tell you whether authorisation is a lookup or a real subsystem.
- **External systems become integrations.** Any story mentioning a third party implies a
  boundary, a failure mode, and usually an ADR.
- **"Cannot", "must not", "only after" clauses are invariants.** These become state machine
  transitions, constraints, and transaction boundaries.
- **Volume and time words are load requirements in disguise.** "Daily reconciliation",
  "real time", "bulk upload" each imply a different processing model.

Produce a short driver table and confirm it before the interview proper. Getting the
drivers wrong invalidates everything downstream, and it is cheap to fix at this stage.

| Driver | Source | Architectural consequence |
|---|---|---|
| Search results in under 2s at p95 | NFR-004 | Read model or search index, not table scans |
| PCI cardholder data never stored | NFR-011 | Tokenised payments, gateway holds the PAN |
| 40 concurrent users, internal only | Interview | Single service is sufficient; no need to distribute |

---

## 3. The question bank

The questions themselves live in `references/question-bank.md`, organised by the fourteen
domains in the SKILL.md risk screen, each with a note on why it matters and when the domain
can be skipped. Pull only from the domains that passed the screen — the bank is deliberately
larger than any single interview should be.

## 4. Questions not worth asking

Interview budget is finite, so do not spend it on decisions that are cheap to change later
or that the team can settle without you:

- Code formatting, linting, folder layout, naming conventions.
- Which logging or testing library to use.
- Anything that is one dependency swap away from being reversed.
- Details that only matter after a decision the user has not made yet — do not ask about
  index strategy before the storage engine is chosen.

The distinction to hold in mind: an architecturally significant decision is one that is
**expensive to reverse**, **hard to hide behind an interface**, or **visible to a
stakeholder**. Everything else is an implementation choice, and belongs to whoever writes
the code.

---

## 5. When the user does not know

Frequently the honest answer is "I don't know yet" — especially on load, growth, and
compliance. That is useful information, not a dead end.

- **Design for the reversible case.** Where the answer is unknown, prefer the option that
  is cheapest to change once the answer arrives, and say so in the ADR.
- **Name the trigger.** "Single instance until sustained load exceeds ~200 requests/second,
  at which point extract the reporting read path" gives the team a concrete signal instead
  of a vague someday.
- **Record it as an open question with an owner**, not as a silent assumption.
- **Estimate out loud and let them correct you.** "For an internal tool at a company your
  size I'd assume under 50 concurrent users — sound right?" is usually answered instantly,
  even by someone who could not have produced the number unprompted.
