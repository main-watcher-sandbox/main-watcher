# Main Watcher — project context

Main Watcher is a reusable component that continuously runs a repo's .NET/xUnit v3
integration tests on `main`. On failure it opens a GitHub issue listing the failing tests
and every push since the last green run. While that issue is open, it blocks the repo's
GitHub merge queue; only PRs labelled `fixes-main` can still merge. It also reports test
durations and the slowest tests.

**Status:** architecture designed (2026-09-15). Build started (2026-09-16): the sandbox
target repo template is in `sandbox/`; the gate is built (MainWatcher#5); the reusable test
workflow is built (MainWatcher#7). The manual Planner and Reporter are built (MainWatcher#9),
with passing and failing sandbox checks verified.

## Where things are

- `docs/architecture/architecture.md` — the main document (ARCH-001). Start here.
- `docs/architecture/decisions/` — ADR-001 to ADR-017.
  - ADR-005 is superseded by ADR-007; ADR-006 is superseded by ADR-009.
  - ADR-013 to ADR-017 were accepted on 2026-09-15 (CQ-10 to CQ-14). They amend
    ADR-002, ADR-003, ADR-008 and ADR-010, which carry a note saying so.
  - Accepted ADRs are never edited. A changed decision gets a new ADR that supersedes or
    amends the old one, plus a note at the top of the old one.
- `docs/architecture/test-strategy.md` — TS-001: scenario tests TS-S1–S18, unit tests
  TS-U1–U15.
- `docs/architecture/artifact-index.md` — what exists, what was deliberately not produced,
  and why.
- `docs/architecture/diagrams/` — PNG renders. The Mermaid sources inside the markdown are
  authoritative.
- `sandbox/` — the sandbox target template (`sample-target/`, steered by `sandbox.json`), its
  merge-queue ruleset, `seed-target.sh`, which pushes it to `main-watcher-sandbox` repos, and
  `publish-public.sh`, which publishes the gate and the reusable test workflow to the public
  `main-watcher-sandbox/gate` repo that the public sandbox targets use.
- `MainWatcher.slnx` — the watcher's .NET projects (`src/`, `tests/`; .NET 10, xUnit v3 on
  Microsoft Testing Platform). `src/MainWatcher.Gate` is the gate's logic;
  `src/MainWatcher.TestRunner` is the deadline-and-retry wrapper the test workflow runs.
- `src/MainWatcher.Core` holds target configuration, GitHub access, eligibility, Planner
  and Reporter logic. `src/MainWatcher.Watcher` runs a manual cycle through `watch.yml`.
- `targets.yml` configures targets. For dispatch, recovery and caller validation, read
  `docs/watcher.md`. Sandbox evidence is in `sandbox/issue-9-validation.md`.
- `templates/main-watcher-gate.yml` — the gate workflow targets copy. It runs
  `.github/actions/gate`, which builds and runs `src/MainWatcher.Gate`.
- `templates/main-watcher-tests.yml` — the test caller targets copy. It calls
  `.github/workflows/run-integration-tests.yml`, whose `.github/actions/test-runner` builds
  `src/MainWatcher.TestRunner`.
- `.github/workflows/` — `ci.yml` (`dotnet test` and actionlint on every PR) and
  `sandbox-lock.yml` (hand-made App-authored locks, sandbox org only), and
  `run-integration-tests.yml`, the reusable test workflow targets call;
  `watch.yml` runs a manual Planner/Reporter cycle for one configured target.
- `.claude/skills/architecture-design/` — the design skill used to produce these documents,
  including its validation scripts.

## Design in one paragraph

**Trigger worker.** A self-hosted .NET `BackgroundService` on Kubernetes, outbound HTTPS
only. Every 60 s it checks each target through the read-only `mw-observer` App. When there
is work, it starts `watch.yml` in the watcher repo through the `mw-doorbell` App
(ADR-010). It raises alerts as `watcher-infra` issues (ADR-012).

**watch.yml.** Using the `main-watcher` App, it:
- creates a check run on the target commit;
- starts the target repo's `main-watcher-tests.yml` with `return_run_details`, and stores
  the run ID in the check run's `external_id`;
- when that run's `main-watcher` job finishes, reads its CTRF artifact and opens, updates or closes the
  `main-broken` lock issue.

**Target repos.**
- Their test workflow calls a shared reusable workflow with `secrets: inherit`, so each
  repo owns its test secrets (ADR-009).
- The results contract is exit code plus CTRF JSON (ADR-007).
- A gate workflow in each target is a required merge-queue check. It fails merge groups
  while an App-authored lock is open, unless every PR in the group is labelled
  `fixes-main`, and it fails open on API errors (ADR-002, ADR-008). It also
  fails open when the lock's lease has expired (ADR-014). When a lock opens, the
  watcher re-runs the gate for merge groups queued before it (ADR-016).

**State and scheduling.**
- All state lives in GitHub: check runs, the lock issue, and hidden markers (ADR-003).
- A green run auto-closes the lock; a human close counts as an override (ADR-004).
- An hourly GitHub schedule is only a backup sweep.
- Recovery rules:
  - the Reporter completes the check run only after writing the lock issue, and replays
    interrupted reports without undoing a human override. The test outcome comes from the
    `main-watcher-test` step, counted only when the `main-watcher-tests-finished` marker
    step shows the tests ran to completion. A failure stays red without CTRF, and a
    timeout is neutral (ADR-013);
  - the watcher renews a 4 h lease on each open lock (ADR-014);
  - reconciliation continues after a lock closes, until merges up to its closure are
    checked, and judges each PR's `fixes-main` label as it was at merge time (ADR-015);
  - opening a lock, or renewing a lapsed one, re-runs the gate for merge groups already in
    the queue; the obligation is written in the same issue update (ADR-016);
  - a head whose newest result is neutral is retested after `poll_interval`, up to 3 times
    (ADR-017).

**Timings (phase 1).** They appear in the run's job summary (the CTRF reporter action) and
in the check run output, and are saved as `timings.json` in the artifact (ADR-011). An own
metrics store (PostgreSQL + Grafana) is deferred.

## Open items before building

1. Run sandbox tests early:
   - TS-S17: the watcher's queue sweep end to end (A-7 itself was confirmed by a spike,
     MainWatcher#6);
   - TS-S14, TS-S16 and TS-S18: the reporting, cancel and retry lifecycle (ADR-013,
     ADR-017), which relies on GitHub's job, step and timeout behaviour.
2. Verify team @-mentions from an App notify the team (TS-S10, R-10).
3. Verify CTRF duration units shown by the reporter action (TS-S13, R-17).
4. Remaining `[assumption]` tags: worker resource sizing and .NET version.

## Suggested next steps

1. The `user-stories` backlog, or go straight to the build.
2. Build order:
   1. Gate workflow and sandbox (retires the biggest risk). Built (MainWatcher#5); TS-S4 and TS-S5
      passed in the sandbox on 2026-09-16.
   2. Reusable test workflow + caller template. Built (MainWatcher#7).
   3. `watch.yml` (Planner and Reporter). Built (MainWatcher#9); automated triggers and
      lock lifecycle remain in later tickets.
   4. Trigger worker.
   5. Onboarding docs.

## Validating the docs after edits

Run from the project root (needs Python 3; diagram checks need Node/npm plus the Python
`playwright` package with Chromium):

```
python .claude/skills/architecture-design/scripts/check_consistency.py docs/architecture
python .claude/skills/architecture-design/scripts/check_freshness.py docs/architecture
python .claude/skills/architecture-design/scripts/render_diagrams.py docs/architecture --check
```

Expected output: four "cites superseded ADR" notices for ADR-005 and ADR-006. They are
intentional history references. `check_freshness.py` should report nothing awaiting
confirmation.

## Conventions

- **IDs:**
  - FR / NFR / C (constraints) / A (assumptions) / R (risks) / Q;
  - CQ = confirmation-queue items (CQ-1 to CQ-14, all settled);
  - TS-S = scenario tests, TS-U = unit tests.
- **Front matter:** every doc has an owner, reviewed date and review-by date. Update
  `reviewed` and the change log in `architecture.md` §20 when you edit.
- **Tags:** `[assumption]` and `[open]` mark unconfirmed statements.

## Agent skills

### Issue tracker

Issues live in GitHub Issues on Actium-Group-Corporation/MainWatcher, via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five default triage labels, used unchanged (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

One set of domain docs for the whole repo: `CONTEXT.md` at the root, and ADRs in `docs/architecture/decisions/`. See `docs/agents/domain.md`.
