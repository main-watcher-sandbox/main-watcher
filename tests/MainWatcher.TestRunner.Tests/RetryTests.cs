using System.Text.Json.Nodes;

namespace MainWatcher.TestRunner.Tests;

// TS-U2: the retry passes the failing test names to --filter-method and records the retry count.
public class RetryTests
{
    static readonly List<string> Log = [];

    [Fact]
    public async Task The_retry_passes_each_failed_test_to_filter_method()
    {
        using var target = new FakeTarget();
        target.Write("first/One.ctrf.json", FakeTarget.Report(("Ns.C", "A", "failed"), ("Ns.C", "B", "passed"), ("Ns.C", "O'Brien", "failed")));
        target.Write("first/Two.ctrf.json", FakeTarget.Report(("Ns.D", "E", "failed"), ("Ns.D", "F", "skipped")));
        target.Write("retry/One.ctrf.json", FakeTarget.Report(("Ns.C", "A", "passed"), ("Ns.C", "O'Brien", "passed")));
        target.Write("retry/Two.ctrf.json", FakeTarget.Report(("Ns.D", "E", "passed")));

        var result = await TestRunner.RunAsync(target.Options(target.FakeDotnetTest(firstExitCode: 2, retryExitCode: 0)), Log.Add);

        Assert.Equal(["Ns.C.A", "Ns.C.O'Brien", "Ns.D.E"], result.RetriedTests);
        Assert.Equal(1, result.RetryCount);
        Assert.Equal(
            ["--ignore-exit-code", "8", "--filter-method", "Ns.C.A", "--filter-method", "Ns.C.O'Brien", "--filter-method", "Ns.D.E"],
            File.ReadAllLines(target.PathOf("retry-args.txt")));
    }

    [Fact]
    public async Task No_retry_when_the_first_attempt_passes()
    {
        using var target = new FakeTarget();
        target.Write("first/One.ctrf.json", FakeTarget.Report(("Ns.C", "A", "passed")));

        var result = await TestRunner.RunAsync(target.Options(target.FakeDotnetTest(firstExitCode: 0, retryExitCode: 0)), Log.Add);

        Assert.Equal(0, result.RetryCount);
        Assert.False(File.Exists(target.PathOf("retry-args.txt")));
    }

    [Fact]
    public async Task No_retry_when_more_tests_fail_than_the_limit()
    {
        using var target = new FakeTarget();
        var failing = Enumerable.Range(0, TestRunner.MaxRetriedTests + 1).Select(i => ("Ns.C", $"T{i}", "failed")).ToArray();
        target.Write("first/One.ctrf.json", FakeTarget.Report(failing));

        var result = await TestRunner.RunAsync(target.Options(target.FakeDotnetTest(firstExitCode: 2, retryExitCode: 0)), Log.Add);

        Assert.True(result.Finished);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, result.RetryCount);
        Assert.False(File.Exists(target.PathOf("retry-args.txt")));
    }

    [Fact]
    public async Task The_reports_keep_every_first_attempt_test_and_record_the_retry()
    {
        using var target = new FakeTarget();
        target.Write("first/One.ctrf.json", FakeTarget.Report(("Ns.C", "A", "failed"), ("Ns.C", "B", "passed"), ("Ns.C", "G", "failed")));
        target.Write("first/Two.ctrf.json", FakeTarget.Report(("Ns.D", "F", "passed")));
        // The retry passes A, fails G again, and leaves Two with no tests, as a filtered run does.
        target.Write("retry/One.ctrf.json", FakeTarget.Report(("Ns.C", "A", "passed"), ("Ns.C", "G", "failed")));
        target.Write("retry/Two.ctrf.json", FakeTarget.Report());

        var result = await TestRunner.RunAsync(target.Options(target.FakeDotnetTest(firstExitCode: 2, retryExitCode: 2)), Log.Add);

        Assert.Equal(2, result.ExitCode);
        var one = target.ReadReport("One");
        var tests = one["results"]!["tests"]!.AsArray().Select(t => t!.AsObject()).ToDictionary(t => t["name"]!.GetValue<string>());
        Assert.Equal("passed", tests["Ns.C.A"]!["status"]!.GetValue<string>());
        Assert.Equal(1, tests["Ns.C.A"]!["retries"]!.GetValue<int>());
        Assert.True(tests["Ns.C.A"]!["flaky"]!.GetValue<bool>());
        Assert.Equal("failed", tests["Ns.C.G"]!["status"]!.GetValue<string>());
        Assert.Equal(1, tests["Ns.C.G"]!["retries"]!.GetValue<int>());
        Assert.Null(tests["Ns.C.G"]!["flaky"]);
        Assert.Null(tests["Ns.C.B"]!["retries"]);
        Assert.Equal(2, one["results"]!["summary"]!["passed"]!.GetValue<int>());
        Assert.Equal(1, one["results"]!["summary"]!["failed"]!.GetValue<int>());

        var two = target.ReadReport("Two");
        Assert.Equal("Ns.D.F", two["results"]!["tests"]![0]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"name":"Ns.C.M(x: 1)","extra":{"type":"Ns.C","method":"M"}}""", "Ns.C.M")]
    [InlineData("""{"name":"Ns.C.M(x: 1)"}""", "Ns.C.M")]
    [InlineData("""{"name":"Ns.C.M"}""", "Ns.C.M")]
    [InlineData("""{"extra":{"type":"Ns.C"}}""", "")]
    public void The_filter_name_is_the_fully_qualified_method(string test, string expected) =>
        Assert.Equal(expected, CtrfReports.FilterName(JsonNode.Parse(test)!.AsObject()));

    [Theory]
    [InlineData("**/TestResults/*.ctrf.json", "TestResults/P.ctrf.json", true)]
    [InlineData("**/TestResults/*.ctrf.json", "tests/P/bin/Debug/net10.0/TestResults/P.ctrf.json", true)]
    [InlineData("**/TestResults/*.ctrf.json", "tests/P/TestResults/sub/P.ctrf.json", false)]
    [InlineData("**/TestResults/*.ctrf.json", "tests/P/TestResults/P.trx", false)]
    [InlineData("out/*.ctrf.json", "out/P.ctrf.json", true)]
    [InlineData("out/*.ctrf.json", "x/out/P.ctrf.json", false)]
    public void The_results_glob_matches_relative_paths(string glob, string path, bool matches) =>
        Assert.Equal(matches, CtrfReports.GlobToRegex(glob).IsMatch(path));
}
