# Sandbox

Material for the scenario-test sandbox, the `main-watcher-sandbox` organisation (TS-001 §3).

| Path | What it is |
|---|---|
| `sample-target/` | Template for the synthetic target repos. Its [README](sample-target/README.md) lists the `sandbox.json` switches |
| `rulesets/main-merge-queue.json` | The merge-queue ruleset applied to `main` in each sandbox target |
| `seed-target.sh` | Pushes the template to a sandbox repo and applies the ruleset. Re-run it to reset a repo |

Seed or reset both targets (needs `gh` logged in as a sandbox org admin):

```
sandbox/seed-target.sh main-watcher-sandbox/sample-target
sandbox/seed-target.sh main-watcher-sandbox/sample-target-slow --slow
```

To check a seeded repo builds and writes CTRF, run its `sandbox-selftest` workflow:

```
gh workflow run sandbox-selftest.yml -R main-watcher-sandbox/sample-target
```
