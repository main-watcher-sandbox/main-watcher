# Sandbox

Material for the scenario-test sandbox, the `main-watcher-sandbox` organisation (TS-001 §3).

| Path | What it is |
|---|---|
| `sample-target/` | Template for the synthetic target repos. Its [README](sample-target/README.md) lists the `sandbox.json` switches |
| `rulesets/main-merge-queue.json` | The merge-queue ruleset applied to `main` in each sandbox target |
| `seed-target.sh` | Pushes the template and the gate workflow to a sandbox repo, creates the `main-broken` and `fixes-main` labels, and applies the ruleset. Re-run it to reset a repo |

Seed or reset both targets (needs `gh` logged in as a sandbox org admin):

```
sandbox/seed-target.sh main-watcher-sandbox/sample-target
sandbox/seed-target.sh main-watcher-sandbox/sample-target-slow --slow
```

To check a seeded repo builds and writes CTRF, run its `sandbox-selftest` workflow:

```
gh workflow run sandbox-selftest.yml -R main-watcher-sandbox/sample-target
```

## The sandbox watcher repo

`main-watcher-sandbox/main-watcher` stands in for this repo. Sandbox targets run the gate
action from it, and its `reporter` environment holds the `main-watcher` App key. Before a
scenario test, push the MainWatcher commit under test to its `main`:

```
git push https://github.com/main-watcher-sandbox/main-watcher.git HEAD:main
```

The gate in seeded targets uses `@main` of that repo; set `WATCHER_REF` when seeding to pin
another ref.

## Hand-made locks

Until `watch.yml` exists, scenario tests that need a lock (TS-S4, TS-S5) open one with the
`sandbox-lock` workflow. It creates a `main-broken` issue authored by `main-watcher[bot]`
with a `lease_until` marker, or closes the open ones:

```
gh workflow run sandbox-lock.yml -R main-watcher-sandbox/main-watcher -f target=sample-target -f action=open -f lease_hours=4
gh workflow run sandbox-lock.yml -R main-watcher-sandbox/main-watcher -f target=sample-target -f action=close
```

A negative `lease_hours` makes an expired lease, for the "LOCK LEASE EXPIRED" path.
