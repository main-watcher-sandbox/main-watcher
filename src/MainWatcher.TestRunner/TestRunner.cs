namespace MainWatcher.TestRunner;

public sealed record TestRunOptions
{
    /// <summary>The target's build-and-test command. Microsoft Testing Platform options must be appendable to it.</summary>
    public required string Command { get; init; }

    /// <summary>The target's timeout, covering the first attempt and the retry together.</summary>
    public required TimeSpan Timeout { get; init; }

    public string ResultsGlob { get; init; } = CtrfReports.DefaultGlob;
    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;
    public string Shell { get; init; } = "bash";
}

/// <param name="Finished">True only when the command, and the retry if any, exited on their own.</param>
/// <param name="ExitCode">The exit code of the last attempt, or <see cref="TestRunner.DeadlineExitCode"/>.</param>
/// <param name="RetriedTests">The names passed to <c>--filter-method</c>; empty when there was no retry.</param>
public sealed record TestRunResult(bool Finished, int ExitCode, IReadOnlyList<string> RetriedTests)
{
    public int RetryCount => RetriedTests.Count > 0 ? 1 : 0;
}

/// <summary>
/// The wrapper the <c>main-watcher-test</c> step runs (ADR-013). It runs the target's command,
/// retries the failed tests once (ADR-007, CQ-4), and enforces the target's timeout itself.
/// </summary>
public static class TestRunner
{
    /// <summary>Exit code when the deadline stopped the tests, as GNU <c>timeout</c> uses.</summary>
    public const int DeadlineExitCode = 124;

    /// <summary>
    /// More failures than this are not retried: a failure that wide is not flaky, and the
    /// names would make the command line too long.
    /// </summary>
    public const int MaxRetriedTests = 100;

    public static async Task<TestRunResult> RunAsync(TestRunOptions options, Action<string> log)
    {
        using var deadline = new CancellationTokenSource(options.Timeout);

        var exitCode = await ShellCommand.RunAsync(options.Shell, options.Command, options.WorkingDirectory, deadline.Token);
        if (exitCode is null)
            return DeadlineReached(options, [], log);
        if (exitCode == 0)
            return new TestRunResult(true, 0, []);

        IReadOnlyList<CtrfReport> firstAttempt;
        IReadOnlyList<string> failed;
        try
        {
            firstAttempt = CtrfReports.Load(options.WorkingDirectory, options.ResultsGlob, log);
            failed = CtrfReports.FailedTestNames(firstAttempt);
        }
        catch (Exception e)
        {
            log($"::warning::Could not read the CTRF reports, so failed tests are not retried: {e.Message}");
            return new TestRunResult(true, exitCode.Value, []);
        }

        if (failed.Count == 0)
        {
            log($"The tests exited with code {exitCode} and the CTRF reports list no failed tests, so nothing is retried.");
            return new TestRunResult(true, exitCode.Value, []);
        }
        if (failed.Count > MaxRetriedTests)
        {
            log($"{failed.Count} tests failed, more than {MaxRetriedTests}, so nothing is retried.");
            return new TestRunResult(true, exitCode.Value, []);
        }

        log($"Retrying {failed.Count} failed test(s) once:\n  {string.Join("\n  ", failed)}");
        var retryExitCode = await ShellCommand.RunAsync(options.Shell, RetryCommand(options.Command, failed), options.WorkingDirectory, deadline.Token);
        if (retryExitCode is null)
            return DeadlineReached(options, failed, log);

        try
        {
            CtrfReports.MergeRetry(firstAttempt, CtrfReports.Load(options.WorkingDirectory, options.ResultsGlob, log), failed);
        }
        catch (Exception e)
        {
            log($"::warning::Could not merge the retry into the CTRF reports: {e.Message}");
        }

        return new TestRunResult(true, retryExitCode.Value, failed);
    }

    /// <summary>
    /// The command with the failed tests as <c>--filter-method</c> filters. Exit code 8 (no
    /// tests ran) is ignored, because projects with no failed tests run nothing.
    /// </summary>
    public static string RetryCommand(string command, IEnumerable<string> failedTests) =>
        command + " --ignore-exit-code 8" + string.Concat(failedTests.Select(name => $" --filter-method {BashQuote(name)}"));

    static string BashQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    static TestRunResult DeadlineReached(TestRunOptions options, IReadOnlyList<string> retried, Action<string> log)
    {
        log($"::error title=Test timeout reached::The tests did not finish within {options.Timeout.TotalMinutes:0.##} min and were stopped.");
        return new TestRunResult(false, DeadlineExitCode, retried);
    }
}
