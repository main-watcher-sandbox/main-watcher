using System.Text.Json;
using System.Text.Json.Nodes;

namespace MainWatcher.TestRunner.Tests;

// TS-U6: timings.json separates wall-clock time from summed per-test time, and carries the retry
// flag. The fixture is xUnit v3's own CTRF output from sandbox/sample-target with flaky_test on:
// two projects run in parallel, the Timing project's 50 ms, 2 s and 20 s tests in parallel, and
// Flaky passed on its retry.
public class TimingsTests
{
    static readonly List<string> Log = [];

    static readonly DateTimeOffset T0 = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    static IReadOnlyList<CtrfReport> Fixture() =>
        CtrfReports.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ParallelWithRetry"), "*.ctrf.json", Log.Add);

    static TimingsInput Input(bool retried = false) => new() { Sha = "abc123", RunId = 42, RunAttempt = 1, Retried = retried };

    [Fact]
    public void Wall_clock_and_summed_test_time_are_kept_separate()
    {
        var timings = Timings.Build(Input(), Fixture());

        // Earliest summary start to latest summary stop, across both projects.
        Assert.Equal(1789582854459 - 1789582834287, timings["tests"]!["wallClockMs"]!.GetValue<long>());
        // Every test's duration: 35 + 38 + 0 + 0 + 0 + 0 in one project, 20044 + 91 + 12 + 2040 in the other.
        Assert.Equal(22260, timings["tests"]!["summedMs"]!.GetValue<long>());
        Assert.Equal(10, timings["tests"]!["count"]!.GetValue<int>());
        Assert.Equal(2, timings["tests"]!["reports"]!.GetValue<int>());
    }

    // ADR-021: the same run on xUnit 4.x in the sandbox, whose timings.json gave these figures.
    [Fact]
    public void Xunit4_reports_give_the_timings_the_sandbox_run_recorded()
    {
        var reports = CtrfReports.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Xunit4ParallelWithRetry"), "*.ctrf.json", Log.Add);

        var timings = Timings.Build(Input(), reports);

        Assert.Equal(20294, timings["tests"]!["wallClockMs"]!.GetValue<long>());
        Assert.Equal(22514, timings["tests"]!["summedMs"]!.GetValue<long>());
        Assert.Equal(11, timings["tests"]!["count"]!.GetValue<int>());
        Assert.Equal(2, timings["tests"]!["reports"]!.GetValue<int>());
        Assert.True(timings["retried"]!.GetValue<bool>());
    }

    [Fact]
    public void A_retry_recorded_in_the_reports_sets_the_retry_flag()
    {
        var timings = Timings.Build(Input(retried: false), Fixture());

        Assert.True(timings["retried"]!.GetValue<bool>());
    }

    [Fact]
    public void The_step_retry_count_sets_the_retry_flag_when_the_reports_do_not_show_it()
    {
        // A retry stopped at the deadline is never merged into the reports.
        using var target = new FakeTarget();
        target.Write("P/TestResults/P.ctrf.json", FakeTarget.Report(("Ns.C", "A", "failed")));
        var reports = CtrfReports.Load(target.Root, CtrfReports.DefaultGlob, Log.Add);

        Assert.True(Timings.Build(Input(retried: true), reports)["retried"]!.GetValue<bool>());
        Assert.False(Timings.Build(Input(retried: false), reports)["retried"]!.GetValue<bool>());
    }

    [Fact]
    public void Queue_wait_and_step_durations_come_from_the_recorded_times()
    {
        var input = Input() with
        {
            RunStarted = T0,
            JobStarted = T0.AddSeconds(12.5),
            RestoreStarted = T0.AddSeconds(30),
            RestoreCompleted = T0.AddSeconds(45),
            TestStarted = T0.AddSeconds(46),
            TestCompleted = T0.AddSeconds(106),
        };

        var timings = Timings.Build(input, Fixture());

        Assert.Equal(12500, timings["queueWaitMs"]!.GetValue<long>());
        Assert.Equal(15000, timings["steps"]!["restoreMs"]!.GetValue<long>());
        Assert.Equal(60000, timings["steps"]!["testMs"]!.GetValue<long>());
        Assert.Equal("abc123", timings["sha"]!.GetValue<string>());
        Assert.Equal(42, timings["runId"]!.GetValue<long>());
        Assert.Equal(1, timings["runAttempt"]!.GetValue<int>());
        Assert.Equal(Timings.SchemaVersion, timings["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Missing_or_reversed_times_and_missing_reports_give_nulls_not_zeros()
    {
        // Restore failed before its end was recorded; the job start is earlier than the run start.
        var input = Input() with { RunStarted = T0.AddSeconds(5), JobStarted = T0, RestoreStarted = T0 };

        var timings = Timings.Build(input, []);

        Assert.Null(timings["queueWaitMs"]);
        Assert.Null(timings["steps"]!["restoreMs"]);
        Assert.Null(timings["steps"]!["testMs"]);
        Assert.Null(timings["tests"]!["wallClockMs"]);
        Assert.Null(timings["tests"]!["summedMs"]);
        Assert.Equal(0, timings["tests"]!["count"]!.GetValue<int>());
        Assert.False(timings["retried"]!.GetValue<bool>());
    }

    [Fact]
    public void The_file_round_trips_as_json()
    {
        var timings = Timings.Build(Input(), Fixture());

        var parsed = JsonNode.Parse(timings.ToJsonString())!;

        Assert.Equal(22260, parsed["tests"]!["summedMs"]!.GetValue<long>());
    }

    [Fact]
    public void The_summary_shows_wall_clock_and_summed_time_in_seconds()
    {
        var timings = Timings.Build(Input() with { RunStarted = T0, JobStarted = T0.AddSeconds(3) }, Fixture());

        var summary = Timings.Summary(timings);

        Assert.Contains("| 3.0 s | unknown | unknown | 20.2 s | 22.3 s | yes |", summary);
    }

    [Fact]
    public void The_job_started_is_found_by_the_job_id_even_when_runner_names_repeat()
    {
        // A repository runner and an organisation runner can share a name within one run.
        using var jobs = JsonDocument.Parse("""
            {"total_count":3,"jobs":[
              {"id":101,"name":"main-watcher-tests / main-watcher","status":"completed","runner_name":"build-1","started_at":"2026-09-16T11:00:00Z"},
              {"id":102,"name":"other / slow","status":"in_progress","runner_name":"build-1","started_at":"2026-09-16T11:30:00Z"},
              {"id":103,"name":"main-watcher-tests / main-watcher","status":"in_progress","runner_name":"build-1","started_at":"2026-09-16T12:00:09Z"}
            ]}
            """);

        Assert.Equal(T0.AddSeconds(9), RunTimes.JobStarted(jobs.RootElement, 103, "build-1"));
        Assert.Null(RunTimes.JobStarted(jobs.RootElement, 999, "build-1"));
    }

    [Fact]
    public void Without_a_job_id_only_a_single_unfinished_job_on_the_runner_counts()
    {
        using var unique = JsonDocument.Parse("""
            {"jobs":[
              {"id":101,"status":"completed","runner_name":"GitHub Actions 7","started_at":"2026-09-16T11:00:00Z"},
              {"id":103,"status":"in_progress","runner_name":"GitHub Actions 7","started_at":"2026-09-16T12:00:09Z"}
            ]}
            """);
        using var ambiguous = JsonDocument.Parse("""
            {"jobs":[
              {"id":102,"status":"in_progress","runner_name":"build-1","started_at":"2026-09-16T11:30:00Z"},
              {"id":103,"status":"in_progress","runner_name":"build-1","started_at":"2026-09-16T12:00:09Z"}
            ]}
            """);

        Assert.Equal(T0.AddSeconds(9), RunTimes.JobStarted(unique.RootElement, null, "GitHub Actions 7"));
        Assert.Null(RunTimes.JobStarted(ambiguous.RootElement, null, "build-1"));
    }
}
