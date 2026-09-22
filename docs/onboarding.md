---
owner: platform-team
reviewed: 2026-09-22
review_by: 2027-03-15
---

# Onboarding a repository

Everything a team needs to put a repository under Main Watcher (FR-1), in the order that keeps the repository
mergeable throughout: the two workflows and the App installs first, then a **dry run** that tests the whole path
while nothing is enforced, and only then the required merge-queue check that can block merges.

Nothing here is undone by getting it wrong half-way: until step 7 the gate is not required, and until step 8 no
cycle runs, so no lock can open. [Rollback](#rollback) covers the rest.

ARCH-001 §10 is the same list in one paragraph; this is the working copy.

## Before you start

| You need | Where |
| --- | --- |
| Admin on the target repository | For its merge-queue ruleset and its secrets |
| Permission to install the organisation's Apps on a repository | `main-watcher` and `mw-observer` |
| Write access to the watcher repository | The `targets.yml` entry, and dispatching `dry-run.yml` |

The target must already merge through a **GitHub merge queue** on `main`: the gate is a required check of that
queue and does nothing without one (ADR-002). Its integration tests must run on GitHub-hosted runners, or on
runners the team owns (A-4), and produce **CTRF** JSON (ADR-007) — xUnit v3 on Microsoft Testing Platform writes
it directly. A suite that finishes in well under 30 minutes keeps detection quick (A-2, NFR-1).

The watcher repository must also allow its reusable workflows to be used by other repositories in the
organisation (ARCH-001 §8); that is set once for the organisation, not per target.

## 1. Install the Apps

Add the repository to the **selected repositories** of:

| App | Permissions it uses | Why |
| --- | --- | --- |
| `main-watcher` | Metadata R, Contents R, Checks W, Issues W, Actions W | Check runs, the lock issue, starting and stopping test runs |
| `mw-observer` | Metadata R, Contents R, Checks R, Actions R, Issues R | The trigger worker's reads |

**Never install either App on all repositories** (R-11): a leaked key then reaches every repository in the
organisation. `mw-doorbell` is installed on the watcher repository only and needs nothing here.

If the entry's `notify` list, or the CODEOWNERS `*` rule it falls back to, names a **team**, `main-watcher` also
needs organisation **Members: read**. Without it the mention renders as a team link and notifies nobody (R-10,
TS-S10) — the lock issue looks right and reaches no one.

## 2. Copy the two workflows

Copy both templates into the target repository, unchanged apart from the one block named below, and merge them
to `main`:

| Template | Copy to |
| --- | --- |
| [`templates/main-watcher-tests.yml`](../templates/main-watcher-tests.yml) | `.github/workflows/main-watcher-tests.yml` |
| [`templates/main-watcher-gate.yml`](../templates/main-watcher-gate.yml) | `.github/workflows/main-watcher-gate.yml` |

In the test caller, set only the inputs under `with` to what this repository needs, and leave everything else —
the job name `main-watcher`, the `run-name`, the `@v1` tag — exactly as the template has it. Those literals are
the contract the Reporter reads a result from (ADR-013), and the `run-name` is how a dispatched run is found
again (R-14).

| Input | Default | Set it when |
| --- | --- | --- |
| `test-command` | `dotnet test --no-restore` | The suite is started another way |
| `restore-command` | `dotnet restore` | Restore needs more than the default |
| `results-glob` | `**/TestResults/*.ctrf.json` | The CTRF reports land elsewhere |
| `timeout-minutes` | 30 | The suite needs longer, up to 340 |
| `global-json-file`, `runs-on` | `global.json`, `ubuntu-latest` | The SDK or the runner differs |

Whatever you set for `test-command`, `results-glob` and `timeout-minutes` must be **literal** values and must
match the `targets.yml` entry in step 4. The Planner compares them before every dispatch and refuses a target
whose caller has drifted, so a mismatch means the repository is never tested.

The gate workflow is copied as it is and updated from the template, never edited in place.

## 3. Set up the tests' secrets

The tests run in the target repository, under its own secrets, and the watcher never sees them (ADR-009, FR-5).
The caller passes `secrets: inherit`, and each inherited secret becomes an environment variable of the same name
for the restore and test steps.

**Prefer OIDC over stored keys** for cloud access, so that no long-lived credential is stored at all (ARCH-001
§8). **It is not reachable today:** the shared test job declares its own `permissions` — `contents: read` and
`actions: read` — and a called job's block caps what its steps get whatever the caller grants, so
`id-token: write` never reaches the tests. Granting it means changing the reusable workflow and every caller
together, in one release, because GitHub fails a run whose called job asks for a permission its caller did not
grant. If this target needs OIDC, raise it with the platform team rather than falling back to a stored key.

The test run holds no Main Watcher credential of any kind, and TS-S8 checks that on every release.

## 4. Add the `targets.yml` entry, with `enabled: false`

Open a pull request on the watcher repository adding one entry:

```yaml
targets:
  - repo: acme/checkout
    test_command: dotnet test --no-restore
    results_glob: '**/TestResults/*.ctrf.json'
    timeout: 30
    poll_interval: 15
    notify: [acme/checkout-maintainers]
    enabled: false
```

[watcher.md](watcher.md#configuration) documents every field. `lock_lease` sits outside the list and is set once
for every target (ADR-014); do not add it to an entry.

Start disabled. An enabled entry is watched from the moment it merges, and a first cycle on a repository that is
not ready opens a lock on it.

**CI validates the entry.** The `targets` job in `ci.yml` parses the committed file with the same parser the
cycles use and prints what each entry was understood to mean, so a bad `repo`, a duplicate, a `timeout` outside
1–340, a non-positive `poll_interval`, an unknown field or a malformed `notify` handle fails the pull request
rather than an hour's sweep. The unit tests parse the file too, so neither can be skipped.

## 5. Dry-run it

The dry run tests the whole path while nothing enforces anything. It creates **no check run**, so nothing it
does can open a lock, and it accepts an entry that is still `enabled: false` — that is what it is for.

```bash
gh workflow run dry-run.yml -f target=acme/checkout
```

It checks, in order, stopping at the first failure:

| Check | What it establishes |
| --- | --- |
| Target entry | What the parsed entry means, echoed back |
| Test caller | The caller exists on `main`, calls the reusable workflow once, and its three execution settings are the entry's |
| Gate workflow | The gate workflow exists on `main` and runs the gate action |
| Test run | The head of `main`, dispatched, produced exactly one discoverable run |
| Test outcome | That run's `main-watcher` job satisfies the ADR-013 step contract |
| CTRF reports | The `main-watcher-ctrf` artifact validates against the CTRF schema (ADR-007) |

The run's job summary lists every check with its detail. A dry run waits up to the target's `timeout` plus 30
minutes, asking GitHub once every 30 seconds while it waits, so it costs tens of requests of the installation's
hourly budget — worth knowing if you are dry-running several repositories in one afternoon (R-13).

**A red suite still passes the dry run.** The contract is what is being tested, and a failing test proves more of
it than a passing one; the outcome line says so, and says that a real cycle would open the lock for that commit.
Get `main` green before step 8 all the same.

What the failures mean:

| Stopped at | Usually |
| --- | --- |
| Test caller | The file is not on `main` yet, or `test-command` / `results-glob` / `timeout-minutes` differ from the entry, or one of them is an expression rather than a literal |
| Gate workflow | The gate was not copied, or was edited until it no longer runs the gate action |
| Test run | Actions is disabled in the target, the caller is not on `main`, or its `run-name` was changed |
| Test outcome | A neutral result: setup failed before the tests, or they did not finish. The detail lists the job's steps |
| CTRF reports | `results_glob` does not match what the suite writes, or the reports are not CTRF |

Fix and dry-run again; it is repeatable and leaves nothing behind.

## 6. Check the timings

The dry run's target run has a job summary with the suite's duration, its slowest tests and any retries
(ADR-011), and a `timings.json` in the same artifact. It is worth a look now: it is the baseline every later run
is compared against, and a suite whose slowest tests dominate the wall clock is the one that will make detection
slow.

## 7. Make the gate a required check, and run TS-S5

Add `main-watcher-gate` to the **required status checks** of the repository's merge-queue ruleset on `main`. The
sandbox's ruleset, [`sandbox/rulesets/main-merge-queue.json`](../sandbox/rulesets/main-merge-queue.json), is a
worked example. The watcher cannot read a ruleset, so this is the one step nothing here verifies for you — which
is why the next one exists.

Run **TS-S5** once against this repository (TS-001 §6): with the merge limit at 2, queue a `fixes-main` pull
request and an unlabelled one together, and confirm the batched group fails the gate. It is what confirms A-5 —
that the gate can identify every pull request in a merge group — for this repository's queue settings.

## 8. Enable it

Set `enabled: true` in the entry and merge. Within a minute the trigger worker starts a cycle, and the first
check run appears on the head of `main`. Tell the team:

- a red `main` opens the `main-broken` issue, which lists the failing tests and every push since the last green
  run, and blocks the merge queue;
- while it is open, only pull requests labelled `fixes-main` merge;
- closing the issue by hand is an override, and is recorded as one (ADR-004);
- a green run closes it by itself.

## Rollback

| What went wrong | Roll back by |
| --- | --- |
| This target misbehaves | `enabled: false` in its `targets.yml` entry |
| A bad trigger worker image | Redeploy the previous image tag (`docs/worker.md`) |
| A bad workflow tag: gate, reusable test workflow or templates | Revert the commit and release again, or move the tag back to the previous commit with `release.yml` |

Disabling an entry stops cycles, but it does **not** close an open lock. Nothing then renews that lock's lease,
so the gate stops enforcing it within `lock_lease` (4 h by default) and merges resume on their own (ADR-014,
NFR-3). To lift it at once, close the `main-broken` issue by hand: that is the override, and the gate ignores a
closed lock immediately.

Moving the workflow tag back changes what every target runs, not just this one. `release.yml` will only move it
to a commit whose `scenario-suite` status passed, which the previous commit's does; see
[release.md](release.md).

## Removing a repository

Delete its `targets.yml` entry, then remove it from the selected repositories of `main-watcher` and `mw-observer`
(R-11). Close any open lock first, or its gate keeps failing merge groups until the lease expires. The target
team can then delete the two workflows and drop `main-watcher-gate` from the ruleset.
