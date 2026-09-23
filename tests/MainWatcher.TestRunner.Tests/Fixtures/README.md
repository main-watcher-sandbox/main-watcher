Real CTRF reports from `sandbox/sample-target`, with `failing_tests` and `flaky_test` set as each test says:

- `ParallelWithRetry/`: xUnit 3.2.2, as the test runner uploaded them in the sandbox after its retry.
- `Xunit4ParallelWithRetry/`: xUnit 4.0.0, the same, from sample-target-9 run 35909100260 (ADR-021).
- `Xunit4Retry/first/`, `Xunit4Retry/retry/`: xUnit 4.0.0's reports before any merge, from the first
  attempt and from the retry of Beta and Flaky. The sandbox uploads only merged reports, so these come
  from a local run of the same template; its Windows paths, machine name and user were rewritten to the
  runner's. The retry's `SampleTarget.Timing.Tests` report is empty, as a filtered run leaves it.
