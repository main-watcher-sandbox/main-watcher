namespace SampleTarget.Tests;

/// <summary>
/// Tests that change how long a run takes or whether it finishes. Each is skipped unless its
/// switch is on, so a normal run stays fast and green.
/// </summary>
public class BehaviourTests
{
    /// <summary><c>slow_suite_minutes</c> &gt; 0: the suite takes at least that long (TS-S2).</summary>
    [Fact]
    public async Task SlowSuite()
    {
        var minutes = SandboxSwitches.Current.SlowSuiteMinutes;
        Assert.SkipUnless(minutes > 0, "slow_suite_minutes is 0");

        await Task.Delay(TimeSpan.FromMinutes(minutes), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <c>hang_test</c>: never returns and ignores cancellation, so only the wrapper's
    /// deadline or a job timeout can stop it (TS-S16 e).
    /// </summary>
    [Fact]
    public void Hang()
    {
        Assert.SkipUnless(SandboxSwitches.Current.HangTest, "hang_test is false");

        Thread.Sleep(Timeout.Infinite);
    }

    /// <summary>
    /// <c>flaky_test</c>: fails on the first attempt of a workflow run and passes on the retry,
    /// so the retry flag can be checked (TS-S13). The attempt is remembered in a temp file keyed
    /// by the run; outside Actions it fails once per machine.
    /// </summary>
    [Fact]
    public void Flaky()
    {
        Assert.SkipUnless(SandboxSwitches.Current.FlakyTest, "flaky_test is false");

        var run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "local";
        var attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "1";
        var marker = Path.Combine(Path.GetTempPath(), $"sample-target-flaky-{run}-{attempt}");

        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
            Assert.Fail("flaky_test: first attempt fails; the retry passes");
        }
    }
}
