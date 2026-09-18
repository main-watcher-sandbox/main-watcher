using System.Text.Json;
using MainWatcher.Core;

namespace MainWatcher.Core.Tests;

/// <summary>
/// TS-U13 timestamped check-run fixtures in <c>Fixtures/eligibility.json</c>. The rule's own tests, the Planner's and the trigger
/// worker's all read this one file, so neither caller can drift from <see cref="Eligibility"/>.
/// </summary>
public static class EligibilityFixtures
{
    public sealed record Case(string Name, string Head, DateTimeOffset Now, TimeSpan PollInterval, bool Force,
        IReadOnlyList<CheckRun> Checks, bool Eligible, bool Capped)
    {
        public override string ToString() => Name;
    }

    public static IReadOnlyList<Case> All { get; } = Load();

    /// <summary>Case names, for <c>[MemberData]</c>; xUnit shows each as its own test.</summary>
    public static TheoryData<string> Names(bool includeForced = true) =>
        new(All.Where(c => includeForced || !c.Force).Select(c => c.Name));

    public static TheoryData<string> Unforced => Names(includeForced: false);

    public static Case Named(string name) => All.Single(c => c.Name == name);

    static Case[] Load()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "eligibility.json")));
        var root = json.RootElement;
        var now = DateTimeOffset.Parse(root.GetProperty("now").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var head = root.GetProperty("head").GetString()!;
        var interval = TimeSpan.FromMinutes(root.GetProperty("poll_interval_minutes").GetInt32());
        return root.GetProperty("cases").EnumerateArray().Select(c => new Case(c.GetProperty("name").GetString()!, head, now, interval,
            c.TryGetProperty("force", out var force) && force.GetBoolean(),
            c.GetProperty("checks").EnumerateArray().Select((r, i) => new CheckRun(100 + i, r.GetProperty("sha").GetString()!,
                r.GetProperty("status").GetString()!, Text(r, "conclusion"), Date(r, "started_at")!.Value, Date(r, "completed_at"),
                Text(r, "external_id"))).ToArray(),
            c.GetProperty("eligible").GetBoolean(), c.TryGetProperty("capped", out var capped) && capped.GetBoolean())).ToArray();
    }

    static string? Text(JsonElement json, string key) => json.TryGetProperty(key, out var value) ? value.GetString() : null;
    static DateTimeOffset? Date(JsonElement json, string key) =>
        Text(json, key) is { } text ? DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : null;
}
