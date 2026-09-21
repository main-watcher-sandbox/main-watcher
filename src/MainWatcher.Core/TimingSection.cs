using System.Globalization;

namespace MainWatcher.Core;

/// <summary>
/// The timing section of a check run's output (ADR-011, FR-6): suite time and its change from the last green run, the run's
/// slowest tests and the retry flag. Durations are converted here, from CTRF's milliseconds, and never taken from the reporter
/// action, whose units were once shown wrongly (R-17). The suite time is also written in a hidden marker, which is how a later
/// run finds the last green run's time: in that run's own check run, where all state lives (ADR-003).
/// </summary>
public static class TimingSection
{
    /// <summary>The suite's wall-clock time in milliseconds, in the check run's marker.</summary>
    public const string SuiteMs = "suite_ms";

    /// <summary>
    /// The newest completed green check run that started before <paramref name="check"/>: the run to compare with. Null when
    /// there is none among <paramref name="checks"/>.
    /// </summary>
    public static CheckRun? LastGreen(IEnumerable<CheckRun> checks, CheckRun check) =>
        checks.Where(c => c.Id != check.Id && c is { Status: "completed", Conclusion: "success" } && c.StartedAt <= check.StartedAt)
            .MaxBy(c => c.StartedAt);

    /// <summary>
    /// The section for a run with these reports. <paramref name="lastGreen"/> is the run to compare with, and
    /// <paramref name="unread"/> says why the check runs holding it could not be read, when they could not.
    /// </summary>
    public static string Write(string repo, CtrfResult reports, CheckRun? lastGreen, string? unread = null)
    {
        if (reports.Timing is not { } timing) return "**Timing**\n\nTimings unknown: the CTRF reports could not be read.";
        var lines = new List<string>
        {
            "**Timing**",
            "",
            $"- Suite time: {Duration(timing.WallClockMs)} wall clock; {Duration(timing.SummedMs)} summed across tests, which run in parallel",
            "- Change from the last green run: " + Change(repo, timing.WallClockMs, lastGreen, unread),
            "- Retried: " + (timing.Retried == 0 ? "no"
                : $"yes, {timing.Retried} failed {(timing.Retried == 1 ? "test was" : "tests were")} run a second time"),
        };
        if (timing.Slowest.Count > 0)
        {
            lines.AddRange(["", $"Slowest {(timing.Slowest.Count == 1 ? "test" : $"{timing.Slowest.Count} tests")} of this run:", "",
                "| Test | Duration |", "| --- | --- |"]);
            lines.AddRange(timing.Slowest.Select(t =>
                $"| {Markdown.Escape(Markdown.Clip(t.Name)).Replace("|", "\\|")}{(t.Retried ? " (retried)" : "")} | {Duration(t.DurationMs)} |"));
        }
        return string.Join("\n", lines) + $"\n\n<!-- main-watcher {SuiteMs}={timing.WallClockMs.ToString(CultureInfo.InvariantCulture)} -->";
    }

    static string Change(string repo, long now, CheckRun? green, string? unread)
    {
        if (unread is not null) return $"unknown, because the earlier check runs could not be read ({Markdown.Escape(unread)})";
        if (green is null) return "none to compare with";
        var at = Markdown.Commit(repo, green.Sha);
        if (!long.TryParse(Markers.Field(green.Summary, SuiteMs), NumberStyles.None, CultureInfo.InvariantCulture, out var then))
            return $"unknown, because the last green run, {at}, recorded no suite time";
        var delta = now - then;
        var percent = then > 0 ? $" ({(delta >= 0 ? "+" : "-")}{Math.Abs(delta) * 100.0 / then:0.0}%)" : "";
        return $"{(delta >= 0 ? "+" : "-")}{Duration(Math.Abs(delta))}{percent} against {Duration(then)} at {at}";
    }

    /// <summary>
    /// Milliseconds as people read them: whole milliseconds under a second, tenths of a second under a minute, then minutes
    /// and seconds. The job summary's reporter shows 20.1 s, 2.1 s and 136 ms for xUnit v3's own values (TS-S13).
    /// </summary>
    public static string Duration(long ms) => ms switch
    {
        < 1000 => $"{ms.ToString(CultureInfo.InvariantCulture)} ms",
        // Truncated to the tenth, so a time just under a minute never reads as 60.0 s.
        < 60_000 => (ms / 100 / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + " s",
        _ => $"{(ms / 60_000).ToString(CultureInfo.InvariantCulture)} min {(ms % 60_000 / 1000).ToString(CultureInfo.InvariantCulture)} s"
    };
}
