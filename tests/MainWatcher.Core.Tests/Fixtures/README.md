Real xUnit v3 3.2.2 CTRF and reusable-caller jobs responses from the sandbox:

- `project-0.json`, `project-1.json`: sample-target run 35131756007.
- `failed-project.json`, `caller-jobs.json`: sample-target run 35131806819.

The jobs fixture intentionally retains GitHub's caller-prefixed job name and literal
step names. The two passing reports come from different test projects.

Real xUnit 4.0.0 CTRF (ADR-021), from sample-target-9 run 35909100260 with `failing_tests: ["Beta"]`
and `flaky_test` on, as the test runner uploaded them after its retry:

- `xunit4-project.json`: `SampleTarget.Timing.Tests`, passing.
- `xunit4-failed-project.json`: `SampleTarget.Tests`; Beta failed twice, Flaky passed on its retry.
- `xunit4-empty-retry.json`: what a filtered retry writes for a project with none of the retried
  tests. It comes from a local run of the same template, with the machine's paths and user rewritten
  to the runner's, like the test runner's `Xunit4Retry` fixtures.

The `jobs-*.json` fixtures are raw jobs responses from the #65 capture on sample-target-7
(`sandbox/issue-65-validation.md`), each wrapped with the time it was read:

- `jobs-failed-final.json`: run 35891997126, read as it completed.
- `jobs-cancelled-final.json`: run 35894067224, cancelled during its tests.
- `jobs-force-cancelled-unsettled.json`, `jobs-force-cancelled-final.json`: run 35894853098, read 1 s
  and 5 s after it completed. The first is already `completed` while its steps are not yet final.
- `jobs-no-runner.json`: run 35895539448, cancelled before it got a runner, with no steps.
