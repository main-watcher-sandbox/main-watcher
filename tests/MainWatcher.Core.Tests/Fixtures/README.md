Real xUnit v3 3.2.2 CTRF and reusable-caller jobs responses from the sandbox:

- `project-0.json`, `project-1.json`: sample-target run 35131756007.
- `failed-project.json`, `caller-jobs.json`: sample-target run 35131806819.

The jobs fixture intentionally retains GitHub's caller-prefixed job name and literal
step names. The two passing reports come from different test projects.

The `jobs-*.json` fixtures are raw jobs responses from the #65 capture on sample-target-7
(`sandbox/issue-65-validation.md`), each wrapped with the time it was read:

- `jobs-failed-final.json`: run 35891997126, read as it completed.
- `jobs-cancelled-final.json`: run 35894067224, cancelled during its tests.
- `jobs-force-cancelled-unsettled.json`, `jobs-force-cancelled-final.json`: run 35894853098, read 1 s
  and 5 s after it completed. The first is already `completed` while its steps are not yet final.
- `jobs-no-runner.json`: run 35895539448, cancelled before it got a runner, with no steps.
