---
id: ADR-009
type: adr
status: accepted
state: target
owner: platform-team
reviewed: 2026-09-15
review_by: 2027-03-15
review_trigger: "a target cannot host a workflow file, or actions: write on targets is rejected by security"
sources: [FR-2, FR-5, ADR-001, ADR-006]
confidence: confirmed
amends: ADR-001
supersedes: ADR-006
---

# ADR-009 — Integration tests run as a workflow in each target repo, started by the watcher

**Deciders:** requester, platform team · **Consulted:** security (App permissions)

## Context

The requester decided on 2026-09-15 that **each target repo manages the secrets its tests
need, its own way** (FR-5).

Under ADR-001 the tests ran in a GitHub Actions run belonging to the watcher repo. A run
can only read the secrets of the repository it belongs to, so every target's secrets would
have had to be copied into the watcher repo. Only the watcher's admins could manage them
there. ADR-006's split-token design existed only because target code ran inside the watcher
repo.

## Decision

**What each target repo contains.** Each target repo has a small workflow,
`main-watcher-tests.yml`:
- it is triggered only by `workflow_dispatch`, with inputs `sha` and `check_run_id`;
- it calls the shared reusable workflow `run-integration-tests.yml` from the watcher repo,
  pinned to a version tag, with `secrets: inherit`.

**What the shared workflow does.** It checks out the exact `sha`, runs the target's test
command with one retry of failed tests, and uploads the CTRF reports as an artifact named
`main-watcher-ctrf`. Each target provides its secrets, OIDC settings or feed credentials in
whatever way it chooses.

**How the watcher starts and tracks runs.**
- It starts the run with the workflow-dispatch API, passing `return_run_details: true`
  (available since February 2026), which returns the run ID.
- It stores that run ID in the check run's `external_id`, so all state stays in GitHub
  (ADR-003).
- When the target's run completes, a later watcher run downloads the artifact and reports.
  The watcher never waits on a running test.

**Identity.** The GitHub App `main-watcher` remains the identity; its private key stays in
the watcher repo's `reporter` environment. Its permissions on target repos are now:

| Permission | Level | Why |
|---|---|---|
| Metadata | Read | Required |
| Contents | Read | Commits, compare, activity, CODEOWNERS |
| Checks | Write | Record outcome per commit |
| Issues | Write | Lock issue |
| Actions | Write | Start the test workflow; read run status and artifacts |

**Latest only.** A target with an in-progress Main Watcher check run is not started again.

## Options considered

### Option A — Tests run in the watcher repo, with secrets copied there (ADR-001 / ADR-006 as written)

It needs no extra file and no `actions` permission. It lost because it fails FR-5: target
teams cannot manage their own secrets.

### Option B — Tests run in the target repo, started by the watcher *(chosen)*

### Option C — Each target chooses A or B

It lost because it doubles the code paths and the security models, and no repo needs A.

## Consequences

**Positive**
- **Teams own their secrets**, including OIDC to cloud providers and private feeds, with no
  platform involvement.
- **Target code never runs anywhere near the App private key.** The split-token design in
  ADR-006 is no longer needed.
- **Test logs appear in the target repo's own Actions tab**, where its developers look.
- **The test logic stays central and versioned** in the reusable workflow.

**Negative**
- **Broader App permission.** `actions: write` on targets also lets the App cancel,
  re-run, enable or disable workflows there (R-11).
- **A second workflow file per target**, plus an organisation setting that lets other
  repos use the watcher repo's reusable workflows.
- **A target team can weaken its own tests.** For example, it can edit its caller workflow
  or its secrets so that tests always pass. This is accepted: the team owns its repo and
  its `main`.
- **Reporting is asynchronous.** Detection time now includes one trigger cycle after the
  run completes (ADR-010).
- **Runs can go missing.** A target run cancelled or deleted by a human leaves a check run
  in progress, which the stale-run rule handles.

**Follow-on work**
- Version the reusable workflow and the caller template.
- Update the onboarding steps.
- Add the new App permission to the security review.

## Verification

| Implemented by | Verified by | Operational control |
|---|---|---|
| Reusable workflow v1, caller template, Planner dispatch, Reporter artifact download | TS-S2, TS-S8, TS-S12 | Stale-run alert |

## Revisit when

A target cannot host the caller workflow, or security rejects `actions: write` on targets.
