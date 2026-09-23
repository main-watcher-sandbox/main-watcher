---
id: ADR-007
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "a target repo adopts a test framework other than xUnit v3"
sources: [FR-3, A-3, ADR-005]
confidence: confirmed
supersedes: ADR-005
---

# ADR-007 — Test script contract: exit code plus CTRF JSON (supersedes ADR-005)

> **Amended by [ADR-021](ADR-021-xunit-4-ctrf.md) on 2026-09-23.** Targets may use xUnit 4.x (the
> `xunit.v3` package at 4.x), whose Microsoft Testing Platform option is `--report-xunit-ctrf
> --report-xunit-ctrf-filename` and which writes every project's report to one `TestResults/`
> folder, so each project needs its own file name. The schema accepts `suite` as a string (3.x) or
> an array of strings (4.x), and the failure list shows xUnit's test class as the suite.

**Deciders:** requester, platform team · **Consulted:** —

## Context

ADR-005 chose JUnit XML because it was the most portable format. We now know that **every
target repo uses .NET with xUnit v3**, which the requester confirmed on 2026-09-15.
xUnit v3 can write Common Test Report Format (CTRF), a JSON format with a published
schema:

- console runner: `-ctrf`;
- MSBuild runner: `Ctrf`;
- Microsoft Testing Platform: `--report-ctrf --report-ctrf-filename`.

These facts came from the xunit.net documentation ("What's New in v3" and "Microsoft
Testing Platform").

ADR-005's main cost was JUnit's varying dialects and the XML parser the Reporter would
need. With a single framework, portability no longer matters, and a validatable JSON
format removes that cost.

## Decision

Each target's script:
- exits non-zero when tests fail;
- writes one CTRF JSON report per test project into a directory set in `targets.yml`
  (`results_glob`, default `**/TestResults/*.ctrf.json`).

The Reporter:
- validates each report against the CTRF schema;
- merges the reports;
- lists tests with status `failed`, showing name, suite and the first line of `message`,
  truncated to 200 characters.

The retry of failed tests (CQ-4) passes the failing test names to the xUnit v3 method
filter, `--filter-method`.

What the watcher concludes from each outcome:

| Script outcome | Watcher result |
|---|---|
| Exit 0 | Green, even if reports show failures; a warning is logged |
| Non-zero exit with valid CTRF | Red; failing tests listed |
| Non-zero exit, CTRF missing or schema-invalid | Red; the issue says "failing tests unknown" and links the log |
| Timeout or setup error | Infrastructure error |

We chose this because it is native to the only framework in use, the Reporter can parse it
without an XML library, and it can be checked mechanically.

## Options considered

### Option A — Exit code plus CTRF JSON *(chosen)*

### Option B — Exit code plus JUnit XML (the ADR-005 decision)

Also native in xUnit v3, and supported by far more tools. It lost because its dialects vary
and it has no official schema. Its portability only matters for other frameworks, and none
are in use.

### Option C — TRX (Visual Studio Test Results)

Native in xUnit v3 and well known in .NET tooling. It lost because it is verbose, much
larger than the other formats, and Microsoft-specific. It brings no benefit for our
Reporter.

## Consequences

**Positive**
- The Reporter parses results with `JSON.parse` and no XML library.
- Onboarding can validate a target's report with the CTRF schema before the gate is made
  required, catching bad output early.
- The same format works whether tests are run with `dotnet test` or the xUnit console
  runner.

**Negative**
- **A non-xUnit-v3 target would need a converter**, or this decision reopened.
- **CTRF is younger and less widely supported** than JUnit if we later want to feed results
  into other tools.
- **Multiple test projects produce multiple files** that must be merged.
- **One more build flag** that each target script must remember to pass. Onboarding checks
  for it.

**Follow-on work**
- Add `results_glob` to the `targets.yml` schema.
- Add CTRF schema validation to the Reporter and to the onboarding dry run.
- Replace the JUnit fixture files in TS-U1 with CTRF fixtures.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Reporter CTRF reader, Runner retry | TS-U1 (CTRF fixtures: single project, multiple projects, invalid file) | Onboarding dry run validates the schema |

## Revisit when

A target adopts a test framework other than xUnit v3, or results need to feed a tool that
cannot read CTRF.
