# Main Watcher — project context

Main Watcher is a reusable component that continuously runs a repo's .NET/xUnit v3
integration tests on `main`. On failure it opens a GitHub issue listing the failing tests
and every push since the last green run. While that issue is open, it blocks the repo's
GitHub merge queue; only PRs labelled `fixes-main` can still merge. It also reports test
durations and the slowest tests.

**Status:** architecture designed (2026-09-15). No code has been written yet.

## Where things are

- `docs/architecture/architecture.md` — the main document (ARCH-001). Start here.
- `docs/architecture/decisions/` — ADR-001 to ADR-016.
  - ADR-005 is superseded by ADR-007; ADR-006 is superseded by ADR-009.
  - ADR-013 to ADR-016 are **proposed**, not accepted (CQ-10 to CQ-13). They amend
    ADR-002, ADR-003, ADR-008 and ADR-010, which carry a note saying so.
  - Accepted ADRs are never edited. A changed decision gets a new ADR that supersedes or
    amends the old one, plus a note at the top of the old one.
- `docs/architecture/test-strategy.md` — TS-001: scenario tests TS-S1–S17, unit tests
  TS-U1–U12.
- `docs/architecture/artifact-index.md` — what exists, what was deliberately not produced,
  and why.
- `docs/architecture/diagrams/` — PNG renders. The Mermaid sources inside the markdown are
  authoritative.
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
- when that run finishes, reads its CTRF artifact and opens, updates or closes the
  `main-broken` lock issue.

**Target repos.**
- Their test workflow calls a shared reusable workflow with `secrets: inherit`, so each
  repo owns its test secrets (ADR-009).
- The results contract is exit code plus CTRF JSON (ADR-007).
- A gate workflow in each target is a required merge-queue check. It fails merge groups
  while an App-authored lock is open, unless every PR in the group is labelled
  `fixes-main`, and it fails open on API errors (ADR-002, ADR-008). Proposed: it also
  fails open when the lock's lease has expired (ADR-014). When a lock opens, the
  watcher re-runs the gate for merge groups queued before it (ADR-016).

**State and scheduling.**
- All state lives in GitHub: check runs, the lock issue, and hidden markers (ADR-003).
- A green run auto-closes the lock; a human close counts as an override (ADR-004).
- An hourly GitHub schedule is only a backup sweep.
- Proposed recovery rules:
  - the Reporter completes the check run only after writing the lock issue, and replays
    interrupted reports without undoing a human override. The test outcome comes from the
    `main-watcher-test` step, so a failure stays red even without CTRF (ADR-013);
  - the watcher renews a 4 h lease on each open lock (ADR-014);
  - reconciliation continues after a lock closes, until merges up to its closure are
    checked, and judges each PR's `fixes-main` label as it was at merge time (ADR-015);
  - opening a lock re-runs the gate for merge groups already in the queue (ADR-016).

**Timings (phase 1).** They appear in the run's job summary (the CTRF reporter action) and
in the check run output, and are saved as `timings.json` in the artifact (ADR-011). An own
metrics store (PostgreSQL + Grafana) is deferred.

## Open items before building

1. Get the requester's decision on CQ-10 to CQ-13 (ADR-013 to ADR-016). Two are real
   trade-offs:
   - CQ-11: a lock lapsing during a long watcher outage, against a lock that stays in
     force until someone closes it;
   - CQ-13: narrowing FR-4 so that a group merging in the seconds after a lock opens is
     reported, not blocked.
2. Confirm the cluster has outbound HTTPS to `api.github.com` and a secret store (A-6).
3. Get security approval for `actions: write` on target repos for the `main-watcher` App
   (R-11), and for Issues: read on `mw-observer` (ADR-014).
4. Run sandbox tests early: TS-S5, batched merge groups and the gate (A-5, R-3); and
   TS-S17, gate re-runs for groups queued before a lock (A-7).
5. Verify team @-mentions from an App notify the team (TS-S10, R-10).
6. Verify CTRF duration units shown by the reporter action (TS-S13, R-17).
7. Remaining `[assumption]` tags: worker resource sizing, .NET and Node versions, sandbox
   organisation name.

## Suggested next steps

1. The `user-stories` backlog, or go straight to the build.
2. Build order:
   1. Gate workflow and sandbox (retires the biggest risk).
   2. Reusable test workflow + caller template.
   3. `watch.yml` (Planner and Reporter).
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
intentional history references. Until CQ-10 to CQ-13 are settled, `check_freshness.py`
also lists their `[unconfirmed]` items and exits 1.

## Conventions

- **IDs:**
  - FR / NFR / C (constraints) / A (assumptions) / R (risks) / Q;
  - CQ = confirmation-queue items (CQ-1 to CQ-9 settled; CQ-10 to CQ-13 pending);
  - TS-S = scenario tests, TS-U = unit tests.
- **Front matter:** every doc has an owner, reviewed date and review-by date. Update
  `reviewed` and the change log in `architecture.md` §20 when you edit.
- **Tags:** `[assumption]` and `[open]` mark unconfirmed statements.
