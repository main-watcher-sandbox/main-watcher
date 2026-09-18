using System.Globalization;
using System.Text.RegularExpressions;

namespace MainWatcher.Core;

/// <summary>
/// The hidden <c>&lt;!-- main-watcher … --&gt;</c> markers Main Watcher keeps its state in, since GitHub holds all of it
/// (ADR-003). They live in lock issue bodies and comments, in alerts, and in a check run's output summary, where the
/// stale-run lifecycle records what it has already asked for (ADR-013 point 5).
/// </summary>
public static class Markers
{
    static readonly Regex Pattern = new(@"<!--\s*main-watcher\s(?<fields>.*?)-->", RegexOptions.Singleline);

    /// <summary>The last value of <paramref name="name"/> across the markers of <paramref name="text"/>.</summary>
    public static string? Field(string? text, string name) => Pattern.Matches(text ?? "").SelectMany(Split)
        .Where(f => f.StartsWith(name + "=", StringComparison.Ordinal)).Select(f => f[(name.Length + 1)..]).LastOrDefault();

    /// <summary>The last value of <paramref name="name"/> read as a UTC time; null when it is missing or unreadable.</summary>
    public static DateTimeOffset? Time(string? text, string name) =>
        DateTimeOffset.TryParse(Field(text, name), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    /// <summary>A time in the form <see cref="Time"/> reads back, whatever the writer's offset.</summary>
    public static string Stamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// Sets <paramref name="fields"/> in the last marker of <paramref name="text"/>, keeping that marker's other fields, or
    /// appends a marker when there is none. The other fields matter: a lock's marker also carries <c>lease_until</c>.
    /// </summary>
    public static string Set(string? text, params (string Name, string Value)[] fields)
    {
        var body = text ?? "";
        var written = string.Join(" ", fields.Select(f => $"{f.Name}={f.Value}"));
        if (Pattern.Matches(body) is not { Count: > 0 } markers) return body + $"\n\n<!-- main-watcher {written} -->";
        var marker = markers[^1];
        var kept = Split(marker).Where(f => !fields.Any(field => f.StartsWith(field.Name + "=", StringComparison.Ordinal)));
        return body[..marker.Index] + $"<!-- main-watcher {string.Join(" ", kept.Append(written))} -->"
            + body[(marker.Index + marker.Length)..];
    }

    static IEnumerable<string> Split(Match marker) =>
        marker.Groups["fields"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
