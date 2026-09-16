using System.Diagnostics;

namespace MainWatcher.TestRunner.Tests;

// TS-U14: the wrapper sets finished only when the test command exits on its own, passes its exit
// code through after the one retry, and at its deadline stops the whole process tree and exits
// non-zero without finished.
public class DeadlineWrapperTests
{
    static readonly List<string> Log = [];

    [Fact]
    public async Task A_passing_command_finishes_with_exit_code_0()
    {
        using var target = new FakeTarget();

        var result = await TestRunner.RunAsync(target.Options("exit 0"), Log.Add);

        Assert.True(result.Finished);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.RetryCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(137)]
    public async Task A_failing_command_with_no_failed_tests_finishes_with_its_exit_code_and_no_retry(int exitCode)
    {
        using var target = new FakeTarget();

        var result = await TestRunner.RunAsync(target.Options($"exit {exitCode}"), Log.Add);

        Assert.True(result.Finished);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(0, result.RetryCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task After_the_retry_the_retry_exit_code_is_passed_through(int retryExitCode)
    {
        using var target = new FakeTarget();
        target.Write("first/P.ctrf.json", FakeTarget.Report(("Ns.C", "A", "failed")));
        target.Write("retry/P.ctrf.json", FakeTarget.Report(("Ns.C", "A", retryExitCode == 0 ? "passed" : "failed")));

        var result = await TestRunner.RunAsync(target.Options(target.FakeDotnetTest(firstExitCode: 2, retryExitCode)), Log.Add);

        Assert.True(result.Finished);
        Assert.Equal(retryExitCode, result.ExitCode);
        Assert.Equal(1, result.RetryCount);
    }

    [Fact]
    public async Task At_the_deadline_the_whole_process_tree_is_stopped_without_finished()
    {
        using var target = new FakeTarget();
        // A child that would write a file after 5 s, and a parent that waits far longer.
        var command = "(sleep 5; echo late > child-survived.txt) & sleep 120; wait";
        var clock = Stopwatch.StartNew();

        var result = await TestRunner.RunAsync(target.Options(command, TimeSpan.FromSeconds(2)), Log.Add);

        Assert.False(result.Finished);
        Assert.Equal(TestRunner.DeadlineExitCode, result.ExitCode);
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"took {clock.Elapsed}");

        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(target.PathOf("child-survived.txt")), "a child process outlived the deadline");
    }

    [Fact]
    public async Task The_deadline_covers_the_retry_too()
    {
        using var target = new FakeTarget();
        target.Write("first/P.ctrf.json", FakeTarget.Report(("Ns.C", "A", "failed")));

        var command = target.FakeDotnetTest(firstExitCode: 2, retryExitCode: 0, retryExtra: "sleep 120");
        var result = await TestRunner.RunAsync(target.Options(command, TimeSpan.FromSeconds(10)), Log.Add);

        Assert.False(result.Finished);
        Assert.Equal(TestRunner.DeadlineExitCode, result.ExitCode);
        Assert.Equal(1, result.RetryCount);
    }
}
