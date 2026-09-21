# Main Watcher — project context

Main Watcher is a reusable component that continuously runs a repo's .NET/xUnit v3
integration tests on `main`. On failure it opens a GitHub issue listing the failing tests
and every push since the last green run. While that issue is open, it blocks the repo's
GitHub merge queue; only PRs labelled `fixes-main` can still merge. It also reports test
durations and the slowest tests.

**Status:** architecture designed (2026-09-15). Build started (2026-09-16): the sandbox
target repo template is in `sandbox/`; the gate is built (MainWatcher#5); the reusable test
workflow is built (MainWatcher#7); test timings (`timings.json` and the CTRF job summary) are
built (MainWatcher#8). The manual Planner and Reporter are built (MainWatcher#9),
with passing and failing sandbox checks verified. A red result opens the lock issue and a
green one closes it (MainWatcher#10); TS-S4 passed again with a real lock. The lock lists
every push since the last green run and each later failure adds a comment
(MainWatcher#11); TS-S2 passed. Interrupted reports are replayed from their markers and a
human close gets an override comment (MainWatcher#12); TS-S14 (a), (b), (d) and TS-S3's
override part passed. Infrastructure and contract errors give a neutral result with a
`watcher-infra` alert and never lock, with a further alert after two in a row
(MainWatcher#13); TS-S16 (a) to (f) passed. The trigger worker starts `watch.yml` when a
target has work (MainWatcher#14); deployed to the sandbox namespace, it drove TS-S1 and
TS-S2 end to end with no hand-run cycle. The worker raises its own de-duplicated
`watcher-infra` alerts, for failing cycles, uncompleted `watch.yml` runs, refused
credentials, a low rate limit and a report owed for more than 15 minutes (MainWatcher#15);
TS-S14 (c) passed. The hourly sweep at minute 17 gives every enabled target a cycle and
raises "trigger worker appears down" and gate fail-open alerts (MainWatcher#16). In the
sandbox a sweep tested a push that had waited two hours with the worker down, and reported a
real `gate-fail-open` check run once; TS-S11 passed. GitHub dropped two of the cron's first
three slots and ran the third two minutes late, which is C-7 measured rather than assumed.
A head that has spent its three neutral attempts now raises a "head untestable" alert and
is tested again only after a push or a forced dispatch (MainWatcher#17); TS-S12 and TS-S18
passed, with the trigger worker running throughout. A target run that passes its queue or run
deadline is cancelled, force-cancelled and finally alerted about, and judged from its steps once
it stops (MainWatcher#18); TS-S16 (g) and (h) passed. Every cycle renews an open lock's lease, and
renewing one that had run out records the lapse, comments and alerts (MainWatcher#19); TS-S7 passed
with a 10-minute sandbox lease, except its last clause, which waited on reconciliation. That
run also showed a fixed one-hour renewal interval asking only after so short a lease had expired,
so the worker now asks at half the lease where that is sooner. Merges made during a lock are
reconciled through its closure, judging each pull request by the label it carried when it merged
(MainWatcher#20); TS-S9 and TS-S15 passed, and the same pass discharged TS-S7's last clause.
`CommittedTargetListParses` now scopes its "watch no sandbox target" clause to this repo, so the
replica is green on the list that makes it the sandbox watcher while the clause still guards the
file committed here (MainWatcher#53). Opening a lock, or renewing a lapsed lease, now re-runs the
gate for the merge groups already queued (MainWatcher#21); TS-S17 passed, confirming A-7 through
the App, and found two faults it fixed: the issue list does not hold a lock created a second
earlier, so the Reporter hands its new locks to the sweep, and the gate read no lease from a lock
that had reported a merge, so it had been failing open for the rest of such a lock's life.
TS-S10 passed on 2026-09-18 (MainWatcher#23) and closed R-10: an App's team @-mention notifies
the team only where the App holds organisation `Members: read`, through `notify` and through the
CODEOWNERS fallback alike, so that permission is an install requirement and no Reporter change
was needed. The check run shows a timing section — suite time, its change from the last green
run, the 5 slowest tests and the retry flag, converted from CTRF milliseconds by the watcher
(MainWatcher#22); TS-S13's check-run half passed, so TS-S13 has passed as a whole. The TS-001 §6 workflow and deployment checklist is now a set of
tests (`SecurityChecklistTests`), and a sandbox target test run found no Main Watcher key or GitHub token in its
own environment (MainWatcher#24). TS-S8 passed on 2026-09-21: `sandbox/ts-s8-credential-scope.sh` showed every
out-of-scope call refused with 403, beside a working control. Its first run found `mw-observer` installed on
`sample-target-slow`, which is not a target (R-11); that App was removed from it. The first run also showed that,
on a public repo, GitHub validates a new issue's body before it checks permission, so issue writes are probed with
an empty update instead.

## Where things are

- `docs/architecture/architecture.md` — the main document (ARCH-001). Start here.
- `docs/architecture/decisions/` — ADR-001 to ADR-018.
  - ADR-005 is superseded by ADR-007; ADR-006 is superseded by ADR-009.
  - ADR-013 to ADR-017 were accepted on 2026-09-15 (CQ-10 to CQ-14). They amend
    ADR-002, ADR-003, ADR-008 and ADR-010, which carry a note saying so.
  - ADR-018 (2026-09-16, MainWatcher#8) amends ADR-011: the job summary's history artifact
    and the test job's `actions: read`.
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
  `src/MainWatcher.TestRunner` is the deadline-and-retry wrapper the test workflow runs, and
  writes `timings.json`.
- `src/MainWatcher.Core` holds target configuration, GitHub access (including GitHub App
  authentication), eligibility, Planner and Reporter logic. `src/MainWatcher.Watcher` runs one
  cycle through `watch.yml`. `src/MainWatcher.Worker` is the trigger worker, with its
  Dockerfile and `smoke-test.sh` beside it and its manifests in `deploy/worker`; read
  `docs/worker.md` for its configuration, work rules, health alerts and deployment.
- `src/MainWatcher.Core/Reconciliation.cs` holds the reconciliation rules; `Planner.Reconcile`
  runs them. `src/MainWatcher.Core/QueueSweep.cs` holds the ADR-016 sweep rules;
  `Planner.SweepQueue` runs them.
- `targets.yml` configures targets. For dispatch, recovery and caller validation, read
  `docs/watcher.md`. Sandbox evidence is in `sandbox/issue-9-validation.md`,
  `sandbox/issue-10-validation.md`, `sandbox/issue-11-validation.md`,
  `sandbox/issue-12-validation.md`, `sandbox/issue-13-validation.md`,
  `sandbox/issue-14-validation.md`, `sandbox/issue-15-validation.md`,
  `sandbox/issue-16-validation.md`, `sandbox/issue-17-validation.md`,
  `sandbox/issue-18-validation.md`, `sandbox/issue-19-validation.md`,
  `sandbox/issue-20-validation.md`, `sandbox/issue-21-validation.md`,
  `sandbox/issue-22-validation.md`, `sandbox/issue-23-validation.md`, `sandbox/issue-24-validation.md` and `sandbox/issue-53-validation.md`.
- `templates/main-watcher-gate.yml` — the gate workflow targets copy. It runs
  `.github/actions/gate`, which builds and runs `src/MainWatcher.Gate`.
- `templates/main-watcher-tests.yml` — the test caller targets copy. It calls
  `.github/workflows/run-integration-tests.yml`, whose `.github/actions/test-runner` builds
  `src/MainWatcher.TestRunner`.
- `.github/workflows/` — `ci.yml` (`dotnet test` and actionlint on every PR) and
  `sandbox-lock.yml` (hand-made App-authored locks, sandbox org only), and
  `run-integration-tests.yml`, the reusable test workflow targets call;
  `watch.yml` runs a Planner/Reporter cycle for one dispatched target, or, on its hourly
  schedule or a dispatch with no target, sweeps every enabled one.
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

1. Run sandbox tests early. None of the scenarios named here is still open. Passed since:
   TS-S17 on 2026-09-18 (MainWatcher#21), which confirmed A-7 through the App and the watcher's
   own sweep; TS-S14 (a), (b) and (d) on 2026-09-17 (MainWatcher#12), TS-S16 (a) to (f) the same
   day (MainWatcher#13), TS-S14 (c) with the worker's alerts (MainWatcher#15), TS-S12 and TS-S18
   with the neutral retry cap (MainWatcher#17), TS-S16 (g) and (h) with the stale-run lifecycle
   (MainWatcher#18), TS-S7 with the lock lease (MainWatcher#19), whose reconciliation clause
   passed with TS-S9 and TS-S15 (MainWatcher#20), and TS-S10 with the team mention
   (MainWatcher#23), and TS-S8 with the credential-scope script (MainWatcher#24).
2. Remaining `[assumption]` tag: worker resource sizing.

## Suggested next steps

1. The `user-stories` backlog, or go straight to the build.
2. Build order:
   1. Gate workflow and sandbox (retires the biggest risk). Built (MainWatcher#5); TS-S4 and TS-S5
      passed in the sandbox on 2026-09-16.
   2. Reusable test workflow + caller template. Built (MainWatcher#7); timings and the
      `report` job built (MainWatcher#8).
   3. `watch.yml` (Planner and Reporter). Built (MainWatcher#9); the lock opens and closes
      (MainWatcher#10), lists pushes since the last green run (MainWatcher#11), and replays
      interrupted reports without undoing overrides (MainWatcher#12), gives
      infrastructure errors a neutral result with an alert (MainWatcher#13), and alerts when a
      head has spent its neutral retries (MainWatcher#17). Automated
      triggers, stale-run cancellation (MainWatcher#18), lease renewal (MainWatcher#19),
      reconciliation through a lock's closure (MainWatcher#20) and the queue sweep
      (MainWatcher#21) are built.
   4. Trigger worker. Built (MainWatcher#14), with its health alerts (MainWatcher#15), the
      hourly backup sweep that watches it in turn (MainWatcher#16) and the stale-run deadlines
      (MainWatcher#18), the lock leases it asks to have renewed (MainWatcher#19), the closed
      locks that still owe reconciliation (MainWatcher#20) and the queue sweeps it keeps asking
      for until they finish (MainWatcher#21).
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
