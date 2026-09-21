---
owner: platform-team
reviewed: 2026-09-21
review_by: 2027-03-15
---

# Issue #24 validation

TS-S8 asks whether each credential can do only what ARCH-001 §8 allows. #24 adds the TS-001 §6 review
checklist, applied to every workflow and deployment file. The work has three parts:

| Part | Where | Status |
| --- | --- | --- |
| The checklist, against the committed files | `SecurityChecklistTests` in `MainWatcher.Core.Tests`, on every PR | Passes |
| A target test run inspects its own environment | `inspect_environment` switch, `EnvironmentTests.NoMainWatcherKey` in the sample target | Passed on 2026-09-21 |
| Each App token is refused outside its scope, the keys are where §8 says, R-11 installations, the live cluster | `sandbox/ts-s8-credential-scope.sh` | **Not yet run**: it needs the worker's keys, which only a person runs with |

## The checklist (TS-001 §6)

Each item is a test, so a change that breaks one fails `ci.yml` rather than waiting for a reviewer. Each test
was checked against a deliberately broken copy of its files, and all six failed:
`pull_request_target` added to the reusable workflow, the reporter action moved to a tag, the `report` job
given `contents: write` and a secret, `watch.yml`'s checkout pointed at `inputs.target`, and the worker given a
`Service`, a `hostPort` and an automounted token.

| Item | Test | Finding |
| --- | --- | --- |
| No `pull_request_target` | `NoWorkflowRunsOnPullRequestTarget`: the triggers of every workflow in `.github/workflows`, `templates` and the sample target | None uses it |
| The reusable workflow requests no App tokens | `ReusableTestWorkflowRequestsNoAppToken`: `run-integration-tests.yml`, the test-runner action and the sandbox upload steps | No `create-github-app-token`, `access_tokens`, `app-id`, `private-key` or Main Watcher key name |
| `watch.yml` runs no target code | `WatchRunsNoTargetCode` | Both checkouts take the watcher at its own commit, with no `repository` or `ref`, and `persist-credentials: false`. No job calls another workflow |
| Third-party actions pinned by SHA | `ThirdPartyActionsArePinnedBySha`, across workflows, templates, composite actions, the sample target and `upload-switches.yml`; `OnlyThisRepoMayUseATag` for the rule itself | Every `actions/*` and `ctrf-io/*` reference is a 40-character SHA. Only this repo's own actions and workflow (and their public sandbox copy) use a tag |
| Worker Secret RBAC restricted | `WorkerSecretHasRestrictedAccess` (manifests); the script (live cluster) | The manifests define no Role, binding or ServiceAccount. The pod runs as `default` with `automountServiceAccountToken: false`, and mounts `trigger-worker-keys` with mode `0440`. The live check, listing who can `get` the Secret, is in the script |
| No inbound Service or Ingress | `WorkerHasNoInboundServiceOrIngress` (manifests); the script (live namespace) | No Service, Ingress, Gateway API route, `hostPort`, `hostNetwork` or `nodePort`, including inside the kustomize patch text |
| `report` job secret-free and read-only | `ReportJobIsSecretFreeAndReadOnly` | Permissions exactly `actions: read`, `contents: read`. No `secrets:` and no `secrets` in any expression. Its token is the job's own `github.token` |
| `main-watcher` and `mw-observer` on selected repositories only (R-11) | The script | Not yet run |

Outside the checklist, one thing turned up. `ci.yml`'s `test` and `lint` jobs check out without
`persist-credentials: false`. They run on `pull_request` with a `contents: read` token and hold no App key, so
this is not a checklist failure, but it is the one checkout in the repo that leaves a token on disk.

## A target test run inspects its own environment

`inspect_environment` makes `NoMainWatcherKey` look for credentials in three places:

- every environment variable, whether its name looks like a key (`MAIN_WATCHER_PRIVATE_KEY`, `MW_OBSERVER_KEY…`,
  `MW_DOORBELL_KEY…`, `…PRIVATE_KEY…`) or its value holds a PEM private key, a GitHub token (`ghs_`, `ghp_`,
  `gho_`, `ghu_`, `ghr_`, `github_pat_`) or a git `extraheader` credential;
- every file up to 256 KB under the runner's work folder, which holds the checkout, the downloaded actions and
  the step scripts;
- the same for the temp folder and the home folder, skipping package caches and toolchains.

It fails naming each variable or file it finds, never its value, and writes what it inspected to the job
summary.

Run locally first, it failed as it should. It found a live token in an environment variable of a developer's
shell and named only the variable.

| Step | Evidence |
| --- | --- |
| Template seeded from `3051cf1` | `e01f7e4` on `sample-target` |
| Switch on | `10949a0` 14:42:42Z |
| Target test run, dispatched by hand through the target's own `main-watcher-tests.yml` and the published reusable workflow, as the watcher does | [35613999502](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35613999502): `main-watcher` `success`, `report` `success` |
| `SampleTarget.Tests.EnvironmentTests.NoMainWatcherKey` in the `main-watcher-ctrf` artifact | `passed`, 2505 ms: it ran and found nothing, rather than being skipped |
| Switch off | `2f6160a` 14:45:16Z |

The test runs inside the `main-watcher-test` step, which is given the target's inherited secrets (the sample
target has none) and no token (ADR-018). So a pass shows that the step `main-watcher` runs a target's code in
holds no Main Watcher key and no GitHub token. The scan covered the runner's temp folder, where the checkout
keeps its credential file while it runs, and found nothing there. The run's job summary, which lists the variable
names and folders inspected, is shown only to signed-in viewers.

## TS-S8: credential scope

Run once, as a sandbox org admin with `kubectl` on the worker's cluster:

```
sandbox/ts-s8-credential-scope.sh
```

It reads the two worker keys from `trigger-worker-keys` into a private temporary folder, mints an installation
token for each App, and prints a table. Each probe of a call an App must not make sends a body GitHub would
reject anyway, so a call that was wrongly allowed returns 422 and changes nothing. A 403 therefore means the
permission is missing, and the 200 controls beside it show that the token itself works.

| App | Must work | Must return 403 |
| --- | --- | --- |
| `mw-observer` | Read `watch.yml` runs and issues in the watcher, and check runs, runs and issues in each target | Dispatch `watch.yml`. Create an issue in the watcher. For each target: dispatch `main-watcher-tests.yml`, create a check run, an issue or a label, write a file |
| `mw-doorbell` | Dispatch `watch.yml` (422 on the non-existent branch, so the permission is there); read watcher issues | Create a check run or write a file in the watcher. For each target: dispatch its tests, create a check run or an issue. Its JWT's `GET /repos/{target}/installation` must be 404: no target token can be minted at all |

It then checks:

- the `main-watcher` key is in the watcher's `reporter` environment and in no repository, target environment
  or organisation secret;
- both Apps are installed on selected repositories only, `mw-observer` on exactly the watcher and the
  `targets.yml` targets and `mw-doorbell` on exactly the watcher (R-11);
- no service account outside the `kube-*` namespaces, and not `system:anonymous`, can `get` the Secret;
- the running Deployment has `automountServiceAccountToken: false`;
- the namespace holds no Service or Ingress.

The `main-watcher` App is not probed: its key is only in the `reporter` environment, which is the point.

Results: not yet recorded.
