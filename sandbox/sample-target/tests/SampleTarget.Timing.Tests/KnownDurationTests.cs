namespace SampleTarget.Timing.Tests;

/// <summary>
/// Tests of known duration, so reported timings can be checked against real values and units
/// (TS-S13, R-17). Skipped when <c>timed_tests</c> is false.
/// </summary>
/// <remarks>
/// Each test is in its own class so xUnit runs them in parallel: the suite's wall-clock time
/// (about 20 s) differs from the summed per-test time (about 22 s), as ADR-011 requires.
/// </remarks>
public class Duration50Milliseconds
{
    [Fact]
    public Task Takes50Milliseconds() => KnownDuration.Run(TimeSpan.FromMilliseconds(50));
}

public class Duration2Seconds
{
    [Fact]
    public Task Takes2Seconds() => KnownDuration.Run(TimeSpan.FromSeconds(2));
}

public class Duration20Seconds
{
    [Fact]
    public Task Takes20Seconds() => KnownDuration.Run(TimeSpan.FromSeconds(20));
}

/// <summary>
/// Always runs. Microsoft Testing Platform exits with code 8 when a project runs no tests, so
/// without it <c>timed_tests: false</c> would turn a green run non-zero.
/// </summary>
public class Baseline
{
    [Fact]
    public void Runs()
    {
    }
}

internal static class KnownDuration
{
    public static Task Run(TimeSpan duration)
    {
        Assert.SkipUnless(SandboxSwitches.Current.TimedTests, "timed_tests is false");

        return Task.Delay(duration, TestContext.Current.CancellationToken);
    }
}
