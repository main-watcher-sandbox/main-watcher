---
owner: platform-team
reviewed: 2026-09-22
review_by: 2027-03-15
---

# Releasing Main Watcher

Three things reach targets and the cluster from this repo, and each needs the scenario suite (TS-S1–S19) to have passed on
the exact commit being released (TS-001 §5). No one may override that.

| What | How targets or the cluster pick it up | Released by |
|---|---|---|
| The workflow tag (`v1`): the gate action, the reusable test workflow and its test runner | Targets' copied templates name `@v1` | `release.yml`, "Move the workflow tag" |
| The gate and test caller templates (`templates/`) | Targets copy them at onboarding; they name `@v1` | The same tag: a template change ships with the tag it names |
| The trigger worker image | `ghcr.io/<owner>/main-watcher-worker:<sha>` in the production overlay | `release.yml`, "Build and push the trigger worker image" |

## Running the suite

From a clean checkout of the commit, pushed to GitHub, on the machine that runs the sandbox worker:

```bash
sandbox/run-scenarios.sh
```

It puts that commit into the sandbox, runs every scenario and writes a report under `sandbox/scenarios/out/`. If every unit
passes, it posts the commit status `scenario-suite` = `success` on the commit. A run that fails posts `failure`, which
withdraws an earlier pass: `release.yml` reads only the newest `scenario-suite` status. A run given `--only`, `--no-deploy`
or a working tree with uncommitted changes posts no `scenario-suite` status, because the sandbox did not run exactly that
commit's full suite. See [sandbox/README.md](../sandbox/README.md), "The scenario suite".

**First passing run: 2026-09-22.** All 25 units passed in 115 minutes, and `1e68709` on `main` carries `scenario-suite`
= `success`, so that commit and its descendants can be released. Until then no commit had one: the suite needed about twice
the sandbox `main-watcher` App installation's 5000 API requests an hour, and part-way through a run the watcher's cycles
were refused. [MainWatcher#60](https://github.com/Actium-Group-Corporation/MainWatcher/issues/60) cut a cycle to about 20
requests ([sandbox/issue-60-validation.md](../sandbox/issue-60-validation.md),
[sandbox/issue-25-validation.md](../sandbox/issue-25-validation.md)).

## Releasing

Run the `release` workflow with the commit's full SHA. Its first job, `suite-passed`, fails unless the commit is on `main`
and its newest `scenario-suite` status is `success`. Only then does it move the tag, or build, smoke-test and push the
worker image, tagged with the SHA and with the workflow tag.

```bash
gh workflow run release.yml -f sha=<commit> -f tag=v1
```

## The reporter pin

A new `ctrf-io/github-test-reporter` pin in `run-integration-tests.yml` can change how the job summary reads history and
ranks the slowest tests (ADR-018), so it needs TS-S13 on the pull request's own commit. The `reporter-pin` job in `ci.yml`
fails such a pull request until its head has the status `scenario-suite/ts-s13` = `success`:

```bash
sandbox/run-scenarios.sh --only TS-S13
```

Then re-run the failed `reporter-pin` job. Any run that includes TS-S13, the full suite too, posts that status.

## One-time setup

The tag must not move any other way, so `v*` tags are protected by the ruleset in
[`.github/rulesets/release-tags.json`](../.github/rulesets/release-tags.json): no one may create, move or delete them,
except the one App that `release.yml` uses. GitHub does not accept GitHub Actions' own token as a ruleset bypass actor
("Actor GitHub Actions integration must be part of the ruleset source or owner organization"), and the organisation allows
no deploy keys, so the tag is moved with an App of its own. Found in the sandbox on 2026-09-21 (MainWatcher#25).

1. Create a GitHub App, `main-watcher-release`, owned by the organisation, with repository permission **Contents: read and
   write** and nothing else, no webhook, installable only on this account. Install it on this repository only.
2. Create the environment `release` in this repository, limited to the `main` branch. Give it the variable `RELEASE_APP_ID`
   (the App's ID) and the secret `RELEASE_APP_PRIVATE_KEY` (a private key generated for the App).
3. Put the App's ID in the ruleset's bypass actor, in place of `0`, and apply it:

   ```bash
   gh api -X POST repos/Actium-Group-Corporation/MainWatcher/rulesets --input .github/rulesets/release-tags.json
   ```

Until this is done, the `workflow-tag` job cannot mint its token and fails, and `v*` tags are protected only by practice.
The worker image needs none of it: `release.yml` is the only workflow that pushes to the registry.
