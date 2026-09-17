namespace MainWatcher.Core;

/// <summary>Which row of the ADR-013 outcome table a completed <c>main-watcher</c> job fell in.</summary>
public enum OutcomeKind
{
    Passed,
    Failed,
    /// <summary>The target run was deleted (404).</summary>
    Unknown,
    /// <summary>The job or step names do not match the contract, or the finished marker has no test result.</summary>
    ContractBroken,
    /// <summary>The tests did not finish: setup failure, deadline, timeout, cancellation or lost runner.</summary>
    InfrastructureError
}

/// <summary>A GitHub check conclusion with the evidence established by the ADR-013 step contract.</summary>
public sealed record TestOutcome(OutcomeKind Kind, string Description)
{
    public string Conclusion => Kind switch
    {
        OutcomeKind.Passed => "success",
        OutcomeKind.Failed => "failure",
        _ => "neutral"
    };

    /// <summary>The check run's output title. For a neutral result it records the kind, which the infrastructure streak reads.</summary>
    public string Title => Outcomes.Title(Kind);
}

/// <summary>ADR-013 outcome table: trust test results only with a successful finished marker.</summary>
public static class Outcomes
{
    public const string TestStep = "main-watcher-test";
    public const string MarkerStep = "main-watcher-tests-finished";

    public static string Title(OutcomeKind kind) => kind switch
    {
        OutcomeKind.Passed => "Tests passed",
        OutcomeKind.Failed => "Tests failed",
        OutcomeKind.ContractBroken => "Outcome contract broken",
        OutcomeKind.InfrastructureError => "Infrastructure error",
        _ => "Outcome unknown"
    };

    /// <summary>The <c>watcher-infra</c> alert title for a neutral outcome on <paramref name="repo"/>; null for red or green.</summary>
    public static string? AlertTitle(OutcomeKind kind, string repo) => kind switch
    {
        OutcomeKind.Unknown => $"Outcome unknown on {repo}",
        OutcomeKind.ContractBroken => $"Outcome contract broken on {repo}",
        OutcomeKind.InfrastructureError => $"Infrastructure error on {repo}",
        _ => null
    };

    public static bool IsTestJob(string name) => name == "main-watcher" || name.EndsWith(" / main-watcher", StringComparison.Ordinal);

    public static TestOutcome? Read(IReadOnlyList<WorkflowJob>? jobs)
    {
        if (jobs is null) return new(OutcomeKind.Unknown, "Outcome unknown: the target run was deleted.");
        var matches = jobs.Where(j => IsTestJob(j.Name)).ToArray();
        if (matches.Length > 1) return new(OutcomeKind.ContractBroken, "Outcome contract broken: more than one `main-watcher` job.");
        if (matches.Length == 0) return new(OutcomeKind.ContractBroken, "Outcome contract broken: the completed run has no `main-watcher` job.");
        var job = matches[0];
        if (job.Status != "completed") return null;
        var tests = job.Steps.Where(s => s.Name == TestStep).ToArray();
        var markers = job.Steps.Where(s => s.Name == MarkerStep).ToArray();
        if (tests.Length > 1 || markers.Length > 1)
            return new(OutcomeKind.ContractBroken, $"Outcome contract broken: a step name appears more than once.\n\n{Steps(job)}");
        if (markers.Length == 0 || markers[0].Conclusion != "success")
            return new(OutcomeKind.InfrastructureError, $"Infrastructure error: the tests did not finish.\n\n{Steps(job)}");
        return tests.SingleOrDefault()?.Conclusion switch
        {
            "success" => new(OutcomeKind.Passed, Title(OutcomeKind.Passed)),
            "failure" => new(OutcomeKind.Failed, Title(OutcomeKind.Failed)),
            _ => new(OutcomeKind.ContractBroken, $"Outcome contract broken: `{MarkerStep}` succeeded without a `{TestStep}` result.\n\n{Steps(job)}")
        };
    }

    /// <summary>The job's step conclusions, in order, as a Markdown list.</summary>
    static string Steps(WorkflowJob job) => job.Steps.Count == 0
        ? $"Job {Markdown.Escape(job.Name)} has no steps."
        : $"Steps of job {Markdown.Escape(job.Name)}:\n\n"
            + string.Join("\n", job.Steps.Select(s => $"- {Markdown.Escape(s.Name)}: {Markdown.Escape(s.Conclusion ?? "no conclusion")}"));
}
