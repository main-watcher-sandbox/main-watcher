---
owner: platform-team
reviewed: 2026-09-18
review_by: 2027-03-15
---

# Issue #53 sandbox validation

The replica's CI, run on 2026-09-18 with the scoped `CommittedTargetListParses` pushed to
`main-watcher-sandbox/main-watcher` and the sandbox target list committed on top of it. No
scenario test is involved: what is being checked is that both repositories can be green on
their own working `targets.yml`.

## Before

The replica's last CI run on its own working state, [35360426576][before] on `6360be27`, was
red: 402 of 403 passed and `CommittedTargetListParses` failed with

```
Assert.DoesNotContain() Failure: Filter matched in collection
Collection: [Target { ... Repo = "main-watcher-sandbox/sample-target", ... }]
```

The run one commit earlier, on `04753077`, passed — that commit carries this repo's empty
`targets.yml`, which is not the tree a scenario test runs against.

## After

| Replica commit | What it carries | CI |
|---|---|---|
| [`a4ec0e8f`][take] take MainWatcher at `b140c00` | the scoped test, this repo's empty list | pass |
| [`9ff455e1`][set] set the sandbox target list | the scoped test, `main-watcher-sandbox/sample-target` watched | [pass][after] — 408 of 408 |

The second row is the acceptance criterion: the replica is green on the list that makes it the
sandbox watcher. `GITHUB_REPOSITORY` is `main-watcher-sandbox/main-watcher` there, so the test
parses the file and skips the sandbox-target clause.

## The clause still bites here

Checked locally against a `targets.yml` holding the replica's sandbox entry:

| `GITHUB_REPOSITORY` | Result |
|---|---|
| `main-watcher-sandbox/main-watcher` | passes — the clause is skipped |
| `Actium-Group-Corporation/MainWatcher` | fails — the clause applies |
| unset (a developer's checkout) | fails — the clause applies |

And with an unparseable file (`typo: true` on a target), the test fails as the replica too, so
a file the sweep could not read still fails the build in both repositories.

[before]: https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35360426576
[after]: https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35361608814
[take]: https://github.com/main-watcher-sandbox/main-watcher/commit/a4ec0e8f
[set]: https://github.com/main-watcher-sandbox/main-watcher/commit/9ff455e1
