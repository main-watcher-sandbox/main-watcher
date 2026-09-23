---
owner: platform-team
reviewed: 2026-09-23
review_by: 2027-03-15
---

# Issue #66 sandbox evidence

Run on 2026-09-23 against `MainWatcher@9dfe5e5`, with `sandbox/run-scenarios.sh --only TS-S4,TS-S5 --no-deploy
--no-status --targets 2`. The sandbox was not redeployed: the change is to the suite and to a comment in `lock.yml`,
and nothing the replica, the gate or the worker runs.

The suite's hand-made lock helpers, `Replica.OpenHandMadeLock` and `CloseHandMadeLocks`, had had no caller since #25.
TS-S5 is now a unit of its own that uses them, running as `docs/onboarding.md` runs it; TS-S4 keeps its real lock.

| Result | Scenarios | Target | Minutes |
| --- | --- | --- | --- |
| PASS | TS-S4 | `sample-target` | 6.2 |
| PASS | TS-S5 | `sample-target-10` | 2.6 |

2 of 2 units passed in 9 min, plus 4 min preparing, with 252 GitHub API calls.

## TS-S5, through `lock.yml`

With the target's entry set to `enabled: false`, so no cycle could close the lock or reconcile against it:

- `lock.yml` opened App-authored lock `sample-target-10#10`, with a `lease_until` marker an hour ahead;
- `fixes-main` #11 and unlabelled #12 were queued back to back, and #12's group was built on #11's, so it held both;
- the gate failed the mixed group for #12, which the queue dropped, and #11 merged on its own;
- `lock.yml` closed #10 as `main-watcher[bot]`, so no override was recorded.

The reset that followed re-enabled the entry, and a green cycle and reconciliation of the closed lock completed
before the suite passed.

## TS-S4, with a real lock

- a failing push opened lock `sample-target#116`;
- the gate failed unlabelled #117's group, and the queue removed it;
- `fixes-main` #118 passed the gate and merged.
