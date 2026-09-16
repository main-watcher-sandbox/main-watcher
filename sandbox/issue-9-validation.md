---
owner: platform-team
reviewed: 2026-09-16
review_by: 2027-03-15
---

# Issue #9 sandbox validation

Validated on 2026-09-16 with watcher implementation `9b59eb5`, deployed to the
private `main-watcher-sandbox/main-watcher` replica. Each result used separate
manual Planner and Reporter dispatches of `watch.yml`.

| Scenario | Target head | Planner | Target tests | Reporter | Check result |
| --- | --- | --- | --- | --- | --- |
| Passing head | `32984f11ad2867387eeff1fb55ed2b06b17ea893` | [35137713231](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35137713231) | [35137785876](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35137785876) | [35137936678](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35137936678) | [success](https://github.com/main-watcher-sandbox/sample-target/runs/104934376498) |
| Deliberately failing Alpha | `726675fe0c651db70362cd5ae54b1d739caa4db4` | [35138089659](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35138089659) | [35138177922](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35138177922) | [35138310578](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35138310578) | [failure](https://github.com/main-watcher-sandbox/sample-target/runs/104935704277) |

Both checks were observed as `in_progress` before reporting, with `external_id`
equal to their dispatched target run ID. The dispatch API returned run details;
the missing-ID fallback is covered by the local regression suite.

The real jobs response identified `main-watcher-tests / main-watcher`. On the
failing run, `main-watcher-test` concluded `failure` and
`main-watcher-tests-finished` concluded `success`. The separate
`main-watcher-tests / report` job concluded `success` without changing the
watcher's failing result. The failing check output was:

```text
Tests failed

- SampleTarget.Tests.OutcomeTests.Alpha (7cec5f72-5cc7-4bca-934e-1827794a3873): sandbox.json failing_tests includes Alpha
```

The target's original `sandbox.json` was saved before testing. The only deliberate
failure switch was `failing_tests: ["Alpha"]`. Commit
`f83f02363e81dbbfce85a662c00ae4597501a8e8` restored the original bytes, verified by
the original and restored Git blob ID `248577d26226bbb013bf1803d279b50f0d0366b1`.
Updates used the expected current file SHA to avoid overwriting concurrent edits.

The restored head also passed end to end: Planner
[35138473699](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35138473699),
target tests
[35138551672](https://github.com/main-watcher-sandbox/sample-target/actions/runs/35138551672),
Reporter
[35138738585](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35138738585),
and completed [success check](https://github.com/main-watcher-sandbox/sample-target/runs/104936949539).
The original configuration blob was verified again after the final Reporter cycle.

Local validation before deployment: all 103 tests passed, and CI-pinned
actionlint 1.7.12 passed for all workflows, caller templates and sandbox workflows.
