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
    InfrastructureError,
    /// <summary>
    /// Nothing to report yet: the job has completed, its marker shows no finished tests, and GitHub has not finished writing its
    /// steps down (ADR-019). Never written to a check run.
    /// </summary>
    StepsNotFinal
}

/// <summary>A GitHub check conclusion with the evidence established by the ADR-013 step contract.</summary>
public sealed record TestOutcome(OutcomeKind Kind, string Description)
{
    public string Conclusion => Kind switch
    {
        OutcomeKind.Passed => "success",
        OutcomeKind.Failed => "failure",
        OutcomeKind.StepsNotFinal => throw new InvalidOperationException("Steps not yet final are not a result: nothing is reported (ADR-019)."),
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

    /// <summary>The step GitHub lists last once it has written a job's steps down (ADR-019).</summary>
    public const string LastStep = "Complete job";

    /// <summary>How long after a job completes its steps may still be unwritten before the job is judged as it stands (ADR-019).</summary>
    public static readonly TimeSpan SettleWait = TimeSpan.FromMinutes(5);

    public static string Title(OutcomeKind kind) => kind switch
    {
        OutcomeKind.Passed => "Tests passed",
        OutcomeKind.Failed => "Tests failed",
        OutcomeKind.ContractBroken => "Outcome contract broken",
        OutcomeKind.InfrastructureError => "Infrastructure error",
        OutcomeKind.StepsNotFinal => "Steps not yet final",
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

    /// <summary>
    /// The ADR-013 outcome table, as amended by ADR-019. Null while the <c>main-watcher</c> job has not completed;
    /// <see cref="OutcomeKind.StepsNotFinal"/> while it has, but would read as "the tests did not finish" only because GitHub
    /// has not yet written its steps down, for up to <see cref="SettleWait"/> after its <c>completed_at</c>.
    /// </summary>
    public static TestOutcome? Read(IReadOnlyList<WorkflowJob>? jobs, DateTimeOffset now)
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
        {
            // Only this row waits: a marker that shows finished tests is proof whatever GitHub still has to write (ADR-019).
            // A job with no completed_at cannot be timed, so it is judged as it stands, as before ADR-019.
            var unsettled = !Final(job) && job.CompletedAt is not null;
            if (unsettled && now - job.CompletedAt < SettleWait)
                return new(OutcomeKind.StepsNotFinal, $"GitHub has not finished writing down the steps of job {Markdown.Escape(job.Name)}.");
            return new(OutcomeKind.InfrastructureError, "Infrastructure error: the tests did not finish."
                + (unsettled ? $" GitHub had still not written down every step {SettleWait.TotalMinutes:0} minutes after the job completed." : "")
                + $"\n\n{Steps(job)}");
        }
        return tests.SingleOrDefault()?.Conclusion switch
        {
            "success" => new(OutcomeKind.Passed, Title(OutcomeKind.Passed)),
            "failure" => new(OutcomeKind.Failed, Title(OutcomeKind.Failed)),
            _ => new(OutcomeKind.ContractBroken, $"Outcome contract broken: `{MarkerStep}` succeeded without a `{TestStep}` result.\n\n{Steps(job)}")
        };
    }

    /// <summary>
    /// Whether GitHub has finished writing the job's steps down: every step concluded, and <see cref="LastStep"/> listed last. A
    /// completed job with no steps, which one cancelled before it got a runner is, is final (ADR-019, sandbox/issue-65-validation.md).
    /// </summary>
    public static bool Final(WorkflowJob job) => job.Steps.Count == 0
        || job.Steps.All(s => s.Conclusion is not null && s.Status is null or "completed") && job.Steps[^1].Name == LastStep;

    /// <summary>The job's step conclusions, in order, as a Markdown list.</summary>
    static string Steps(WorkflowJob job) => job.Steps.Count == 0
        ? $"Job {Markdown.Escape(job.Name)} has no steps."
        : $"Steps of job {Markdown.Escape(job.Name)}:\n\n"
            + string.Join("\n", job.Steps.Select(s => $"- {Markdown.Escape(s.Name)}: {Markdown.Escape(s.Conclusion ?? "no conclusion")}"));
}
