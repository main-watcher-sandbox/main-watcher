---
id: ADR-021
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-23
review_by: 2027-03-15
review_trigger: "a target moves to an xUnit major version other than 3.x or 4.x, xUnit renames its CTRF option again, or the vendored CTRF schema's pin is changed"
sources: [FR-3, FR-6, C-5, ADR-007, ADR-011, R-17]
confidence: confirmed
amends: ADR-007
---

# ADR-021 — Targets may use xUnit 4.x, whose CTRF option and `suite` differ (amends ADR-007)

**Deciders:** requester (MainWatcher#71), platform team · **Consulted:** —

## Context

ADR-007 made CTRF the results contract because every target used xUnit v3, and named its Microsoft
Testing Platform (MTP) option, `--report-ctrf --report-ctrf-filename`. The production targets use
xUnit 4.0.0, which still ships as the `xunit.v3` package. The sandbox and every fixture used 3.2.2.

The same sandbox template was run on both versions on 2026-09-23 (MainWatcher#71). It showed:

- **The option was renamed.** 4.x runs on MTP 2.x (`xunit.v3.mtp-v2`, MTP 2.3.3), and its result
  writers register as `report-xunit-<id>`, so CTRF is `--report-xunit-ctrf
  --report-xunit-ctrf-filename`. MTP 2 rejects the old option: the run exits with code 5, runs no
  tests and writes no report.
- **Reports share one folder.** Under `dotnet test`, 4.x writes every project's report to
  `TestResults/` at the repository root, not to each project's `bin/…/TestResults/`. The default
  `results_glob`, `**/TestResults/*.ctrf.json`, matches both.
- **`suite` became an array.** 3.x writes a GUID string. 4.x writes an array led by two hashes:
  `[assembly, collection, "Namespace.Class", "Method"]`. Later CTRF schema revisions define `suite`
  as an array only. The vendored schema, pinned to an earlier revision, allowed only a string, so
  every 4.x report failed validation. Nothing else in a 4.x report failed.
- **The rest is unchanged or only added to.** `summary`, `duration`, `extra.type` and
  `extra.method` are as before. Per-test `start` and `stop` and a top-level `generatedBy` are new.
  A skipped test's reason moved from `extra.reason` to `message`. `--filter-method` and
  `--ignore-exit-code 8` work as before, and a filtered retry still leaves an empty, valid report
  for a project with none of the retried tests.

A 4.x target was therefore still judged correctly, from its steps (ADR-013), but with its failing
tests "unknown", no timing section (ADR-011), and an onboarding dry run that could never pass.

## Decision

**1. A target may use xUnit 3.x or 4.x.** C-5 reads "the `xunit.v3` package, 3.x or 4.x". The
contract stays exit code plus one CTRF report per test project (ADR-007).

**2. The CTRF option depends on the version.** A 4.x target passes `--report-xunit-ctrf
--report-xunit-ctrf-filename <name>`, a 3.x target `--report-ctrf --report-ctrf-filename <name>`.
Because 4.x puts every project's report in one folder, the file name must differ per project:
`$(MSBuildProjectName).ctrf.json`, as `docs/onboarding.md` gives it. A fixed name would leave one
report standing for the whole suite.

**3. The schema accepts both `suite` shapes.** The vendored schema keeps its pin and allows a
string, or a non-empty array of strings. A newer upstream pin would reject every 3.x report, so it
is patched instead, and `src/MainWatcher.Core/Schema/README.md` says so.

**4. The failure list shows the test class as the suite.** The Reporter takes xUnit's `extra.type`
when present. That is readable in both versions, where the `suite` value is a GUID (3.x) or an array
led by hashes (4.x). Without `extra.type`, it shows the `suite` string, or the array's last element,
which CTRF defines as the immediate parent.

**5. The sandbox runs what production runs.** `sandbox/sample-target` uses 4.0.0, and 3.2.2 is kept
only in the unit fixtures.

## Options considered

### Option A — Accept both shapes in the patched pin *(chosen)*

It changes one field of the schema and one line of the reader, and both versions keep working.

### Option B — Vendor the newest upstream schema

It follows upstream. It lost because its `suite` is an array only, so every 3.x report would be
invalid, and it brings other changes that were not measured against either version.

### Option C — Rewrite reports in the test runner before upload

Turn `suite` into a string in `MainWatcher.TestRunner`, so the watcher's schema stays unchanged. It
lost because the uploaded reports would no longer be xUnit's own, which TS-S13 compares against. It
would also change the job summary's input, and the watcher would still have to accept reports from
a target run on an older workflow tag.

### Option D — Keep targets on 3.x

It needs no change here. It lost because the production targets have already moved to 4.0.0.

## Consequences

**Positive**
- 4.x targets get their failing tests listed, a timing section and a working dry run.
- The failure list shows a class name instead of a GUID, for 3.x targets too.
- The sandbox exercises the version production uses.

**Negative**
- **Two option names to document.** Onboarding has to say which one applies. A wrong one is loud:
  MTP exits 5 and the dry run fails on missing CTRF.
- **A locally patched schema.** It no longer matches any upstream revision exactly, so a pin update
  must re-apply the patch and be checked against both versions' fixtures.

**Follow-on work**
- None.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| The vendored schema; `CtrfReader`; `sandbox/sample-target`; `docs/onboarding.md` | TS-U1 and TS-U2 over real 4.0.0 fixtures beside the 3.2.2 ones; TS-S13 on 4.0.0; the scenario suite on 4.0.0 targets | Onboarding dry run's CTRF check |

## Revisit when

- A target moves to an xUnit major version other than 3.x or 4.x, or xUnit renames the option again.
- The vendored schema's pin is changed.
