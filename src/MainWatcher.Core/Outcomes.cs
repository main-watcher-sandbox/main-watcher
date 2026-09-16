namespace MainWatcher.Core;

/// <summary>A GitHub check conclusion with the evidence established by the ADR-013 step contract.</summary>
public sealed record TestOutcome(string Conclusion, string Description);

/// <summary>ADR-013 outcome table: trust test results only with a successful finished marker.</summary>
public static class Outcomes
{
    public static string Title(string conclusion) => conclusion switch
    {
        "success" => "Tests passed",
        "failure" => "Tests failed",
        _ => "Outcome unknown"
    };

    public static bool IsTestJob(string name) => name == "main-watcher" || name.EndsWith(" / main-watcher", StringComparison.Ordinal);

    public static TestOutcome? Read(IReadOnlyList<WorkflowJob>? jobs)
    {
        if (jobs is null) return new("neutral", "outcome unknown: target run was deleted");
        var matches = jobs.Where(j => IsTestJob(j.Name)).ToArray();
        if (matches.Length > 1) return new("neutral", "outcome contract broken: multiple main-watcher jobs");
        if (matches.Length == 0) return new("neutral", "outcome contract broken: main-watcher job missing from completed run");
        var job = matches[0];
        if (job.Status != "completed") return null;
        var tests = job.Steps.Where(s => s.Name == "main-watcher-test").ToArray();
        var markers = job.Steps.Where(s => s.Name == "main-watcher-tests-finished").ToArray();
        if (tests.Length > 1 || markers.Length > 1) return new("neutral", "outcome contract broken: duplicate step names");
        if (markers.Length == 0 || markers[0].Conclusion != "success") return new("neutral", "Infrastructure error: tests did not finish");
        return tests.SingleOrDefault()?.Conclusion switch
        {
            "success" => new("success", Title("success")),
            "failure" => new("failure", Title("failure")),
            _ => new("neutral", "outcome contract broken: finished marker without a test result")
        };
    }
}
