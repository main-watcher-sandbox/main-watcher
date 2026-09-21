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
| Each App token is refused outside its scope, the keys are where §8 says, R-11 installations, the live cluster | `sandbox/ts-s8-credential-scope.sh`, and `app-installations.yml` for `main-watcher` | The second run passed on 2026-09-21. The PR #59 review then found two gaps, both now closed in the script: the Secret check read only `get` and counted a failed query as a denial, and `main-watcher`'s installation was not checked. **A third run is owed** |

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
| Worker Secret RBAC restricted | `WorkerSecretHasRestrictedAccess` (manifests); the script (live cluster) | The manifests define no Role, binding or ServiceAccount. The pod runs as `default` with `automountServiceAccountToken: false`, and mounts `trigger-worker-keys` with mode `0440`. The live check, who can `get`, `list` or `watch` Secrets in the namespace, is in the script |
| No inbound Service or Ingress | `WorkerHasNoInboundServiceOrIngress` (manifests); the script (live namespace) | No Service, Ingress, Gateway API route, `hostPort`, `hostNetwork` or `nodePort`, including inside the kustomize patch text |
| `report` job secret-free and read-only | `ReportJobIsSecretFreeAndReadOnly` | Permissions exactly `actions: read`, `contents: read`. No `secrets:` and no `secrets` in any expression. Its token is the job's own `github.token` |
| `main-watcher` and `mw-observer` on selected repositories only (R-11) | The script | `mw-observer` is on exactly the watcher and `sample-target` (second run), after `sample-target-slow` was removed. `mw-doorbell` is on the watcher only. `main-watcher`: not yet run. The script now starts `app-installations.yml`, which lists the App's installation from the `reporter` environment |

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

Run as a sandbox org admin with `kubectl` on the worker's cluster:

```
sandbox/ts-s8-credential-scope.sh
```

It reads the two worker keys from `trigger-worker-keys` into a private temporary folder, mints an installation
token for each App, and prints a table. Each probe of a call an App must not make sends a body GitHub would
reject anyway, so a call that was wrongly allowed returns 422 and changes nothing. A 403 therefore means the
permission is missing, and the 200 controls beside it show that the token itself works. Issue writes on a
target are the exception, as the run below found. They are now probed with an empty update to an existing issue,
which changes nothing even if it is allowed.

| App | Must work | Must return 403 |
| --- | --- | --- |
| `mw-observer` | Read `watch.yml` runs and issues in the watcher, and check runs, runs and issues in each target | Dispatch `watch.yml`. Create an issue in the watcher. For each target: dispatch `main-watcher-tests.yml`, create a check run or a label, update an issue, write a file |
| `mw-doorbell` | Dispatch `watch.yml` (422 on the non-existent branch, so the permission is there); read watcher issues | Create a check run or write a file in the watcher. For each target: dispatch its tests, create a check run or a label, update an issue. Its JWT's `GET /repos/{target}/installation` must be 404: no target token can be minted at all |

It then checks:

- the `main-watcher` key is in the watcher's `reporter` environment and in no repository, target environment
  or organisation secret;
- both Apps are installed on selected repositories only, `mw-observer` on exactly the watcher and the
  `targets.yml` targets and `mw-doorbell` on exactly the watcher (R-11);
- the `main-watcher` App is installed on selected repositories only, exactly the `targets.yml` targets. The
  script cannot mint that App's token, so it starts the watcher's `app-installations.yml`, waits for it and reads
  its artifact. That workflow runs in the `reporter` environment, checks out no code, and asks for a
  metadata-read token covering the App's whole installation;
- no service account outside the `kube-*` namespaces, and not `system:anonymous`, can `get` the Secret, or
  `list` or `watch` Secrets in its namespace, which returns their contents just the same. Only an explicit `no`
  counts as denied: a query that errors, or a namespace or service account list that cannot be read, fails
  the row;
- the running Deployment has `automountServiceAccountToken: false`;
- the namespace holds no Service or Ingress.

The `main-watcher` App is not probed: its key is only in the `reporter` environment, which is the point.

## The first run, 2026-09-21T15:14:17Z

Run by a sandbox org admin against `main-watcher-sandbox/main-watcher`, whose `targets.yml` lists one target,
`main-watcher-sandbox/sample-target`. The keys came from the cluster's `trigger-worker-keys` Secret. **27 pass,
3 fail, 1 skipped.**

### mw-observer

| Result | Call | Got |
| --- | --- | --- |
| PASS | `GET` watcher `actions/workflows/watch.yml/runs` | 200 |
| PASS | `GET` watcher `issues` | 200 |
| PASS | `POST` watcher `actions/workflows/watch.yml/dispatches` | 403 |
| PASS | `POST` watcher `issues` | 403 |
| PASS | `GET` target `commits/main/check-runs` | 200 |
| PASS | `GET` target `actions/runs` | 200 |
| PASS | `GET` target `issues` | 200 |
| PASS | `POST` target `actions/workflows/main-watcher-tests.yml/dispatches` | 403 |
| PASS | `POST` target `check-runs` | 403 |
| FAIL, inconclusive | `POST` target `issues` with an empty body | 422 "Invalid request" |
| PASS | `POST` target `labels` | 403 |
| PASS | `PUT` target `contents/ts-s8-probe.txt` | 403 |

### mw-doorbell

| Result | Call | Got |
| --- | --- | --- |
| PASS | `POST` watcher `actions/workflows/watch.yml/dispatches`, non-existent branch | 422: the dispatch was allowed and failed only on the branch |
| PASS | `GET` watcher `issues` | 200 |
| PASS | `POST` watcher `check-runs` | 403 |
| PASS | `PUT` watcher `contents/ts-s8-probe.txt` | 403 |
| PASS | `GET /repos/main-watcher-sandbox/sample-target/installation` with its JWT | 404: no token for the target can be minted |
| PASS | `POST` target `actions/workflows/main-watcher-tests.yml/dispatches` | 403 |
| PASS | `POST` target `check-runs` | 403 |
| FAIL, inconclusive | `POST` target `issues` with an empty body | 422 "Invalid request" |

### Installations, keys and cluster

| Result | Check | Detail |
| --- | --- | --- |
| PASS | `mw-observer` on selected repositories | `selected` |
| **FAIL** | `mw-observer` repositories | Expected the watcher and `sample-target`. Found those two plus `main-watcher-sandbox/sample-target-slow` |
| PASS | `mw-doorbell` on selected repositories | `selected` |
| PASS | `mw-doorbell` repositories | The watcher only |
| PASS | `main-watcher` key in the watcher's `reporter` environment | `MAIN_WATCHER_PRIVATE_KEY` |
| PASS | No Main Watcher key in the watcher's repository secrets | No secrets |
| SKIP | No Main Watcher key in the organisation's secrets | Could not list them: the `gh` token lacked the organisation secrets permission (HTTP 403) |
| PASS | No Main Watcher key in `sample-target`'s repository secrets | No secrets, and the target has no environments |
| PASS | No service account can `get` `main-watcher-sandbox/trigger-worker-keys` | 3 subjects checked (`system:anonymous` and every service account outside `kube-*`) |
| PASS | Worker pod gets no service account token | `automountServiceAccountToken: false` |
| PASS | No Service or Ingress in `main-watcher-sandbox` | None |

### What the failures mean

**The two issue-creation rows are a flaw in the probe, not a permission leak.** `mw-doorbell` got the same 422
as `mw-observer`, but its token cannot reach `sample-target` at all: the App is not installed there, and its
JWT's installation lookup returned 404 two rows earlier. So on a public repository, GitHub validates the body of
a new issue before it checks the caller's permission, and an empty issue says nothing about the permission. The
row is not needed for `mw-observer`: `POST …/labels` needs the same Issues write permission and was refused with
403. The script now sends an empty update to an existing issue instead, and adds the labels probe for
`mw-doorbell`. The second run shows both Apps refused on both.

**`mw-observer` on `sample-target-slow` is a real R-11 deviation.** R-11's security approval of Issues: read for
`mw-observer` required the App to be installed only on the repositories being watched, plus the watcher.
`sample-target-slow` is a sandbox target variant (TS-001 §3) that the replica's `targets.yml` does not list.
Either remove it from the App's selected repositories, or list it in `targets.yml` while a scenario uses it. The
exposure is small, since the App only reads and the repository is public and synthetic. But the same drift in
production would give a read-only token access to a repository nobody is watching, which is exactly what R-11's
condition exists to prevent.

**Organisation secrets were not checked.** Re-run with a token that has `admin:org`
(`gh auth refresh -s admin:org`), or confirm by hand under the organisation's Actions secrets.

The `main-watcher` App's installations are not checked at all: the script cannot mint its token, because its key
exists only in the `reporter` environment. Check them by hand under the organisation's installed GitHub Apps.

## The second run, 2026-09-21T15:27:05Z

Run after these three changes:

- the script's issue probes were changed (`951cf5d`);
- `sample-target-slow` was removed from `mw-observer`'s selected repositories;
- the `gh` token was given `admin:org`.

**All 32 checks pass.** The rows unchanged from the first run give the same codes and are not repeated.

| Result | Check | Got |
| --- | --- | --- |
| PASS | `mw-observer`: `PATCH` target `issues/59` with an empty body | 403 |
| PASS | `mw-doorbell`: `PATCH` target `issues/59` with an empty body | 403 |
| PASS | `mw-doorbell`: `POST` target `labels` | 403 |
| PASS | `mw-observer` repositories | `main-watcher-sandbox/main-watcher`, `main-watcher-sandbox/sample-target`: exactly the watcher and `targets.yml` |
| PASS | No Main Watcher key in the organisation's secrets | No secrets |

Every other row passed as in the first run:

- **`mw-observer`:** 200 on all five reads. 403 on dispatching `watch.yml` and the target's tests, on creating a
  watcher issue, a target check run or a label, and on writing a file.
- **`mw-doorbell`:** 422 on dispatching `watch.yml` (allowed, failing only on the branch) and 200 on reading
  watcher issues. 403 on a watcher check run or file write, 404 on its target installation lookup, and 403 on
  the target's dispatch and check run.
- **Installations:** both Apps are on selected repositories only, and `mw-doorbell` is on the watcher alone.
- **Keys:** `MAIN_WATCHER_PRIVATE_KEY` is in the `reporter` environment and in no repository or target secret.
- **Kubernetes:** no service account can read the Secret, the pod gets no service account token, and there is no
  Service or Ingress.

## PR #59 review

The review found two gaps in what the second run could show.

- **The Secret check could pass without evidence.** It asked `kubectl auth can-i get` only, and counted any
  answer but `yes` as a denial, including an impersonation error or an API failure. A namespace or service
  account list that could not be read was also silently empty. And `list` or `watch` on Secrets gives the same
  access as `get`, since both return the contents. The script now asks all three verbs of every subject, and
  fails the row unless every answer is an explicit `no` and every namespace's service accounts were listed. The
  rewritten section was run against a fake `kubectl` in four cases:

  | Fake `kubectl` | Result |
  | --- | --- |
  | Every query answers `no` | PASS, 9 queries |
  | Every query errors (`impersonation forbidden`) | FAIL, "not every query was answered" |
  | `list secrets` answers `yes` | FAIL, the three subjects allowed to `list` |
  | Namespaces cannot be listed | FAIL twice: the discovery row, and the access row, since the subjects are incomplete |

  The old code gave PASS in the second and fourth cases.
- **`main-watcher`'s installation was not verified.** The script now dispatches `app-installations.yml` in the
  watcher repo and compares the App's repositories with `targets.yml`, which should list them exactly: the App
  is installed on targets only (ARCH-001 §8). A run that cannot be started, found, finished or downloaded fails
  the row.

## TS-S8 status

The second run passed every check it made. It is not yet a full pass, because it asked only `get` and did not
cover `main-watcher`. What remains:

1. Put this tree on the replica's `main` (sandbox/README.md), so that `app-installations.yml` exists there, and
   set the replica's `targets.yml` back to the sandbox target.
2. Re-run `sandbox/ts-s8-credential-scope.sh` and record the new rows: `main-watcher installed on selected
   repositories`, `main-watcher repositories`, `Service accounts listed`, and the get, list and watch row.
