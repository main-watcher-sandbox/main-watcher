using System.Text.Json;
using System.Text.Json.Nodes;

namespace MainWatcher.Scenarios.Sandbox;

/// <summary>
/// What a clean sandbox target holds, read from this checkout exactly as <c>sandbox/seed-target.sh</c> writes it: the
/// template's <c>sandbox.json</c>, and the caller and gate workflows pointed at the public gate repo.
/// </summary>
public sealed class Templates(string root)
{
    public const string GateRepo = "main-watcher-sandbox/gate";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    string Read(string path) => File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n");

    /// <summary>The template's switches: every one off.</summary>
    public JsonObject DefaultSwitches() => JsonNode.Parse(Read("sandbox/sample-target/sandbox.json"))!.AsObject();

    public static string Format(JsonObject switches) => switches.ToJsonString(Indented).Replace("\r\n", "\n") + "\n";

    /// <summary>The caller workflow, as seeded, optionally on a variant branch, with another timeout or runner label.</summary>
    public string Caller(string gateRef = "main", int timeoutMinutes = 30, string? runsOn = null)
    {
        var text = Read("templates/main-watcher-tests.yml").Replace(
            "Actium-Group-Corporation/MainWatcher/.github/workflows/run-integration-tests.yml@v1",
            $"{GateRepo}/.github/workflows/run-integration-tests.yml@{gateRef}");
        if (timeoutMinutes != 30) text = Replace(text, "      timeout-minutes: 30\n", $"      timeout-minutes: {timeoutMinutes}\n");
        // The caller validator reads only test-command, results-glob and timeout-minutes, so an extra input is accepted.
        if (runsOn is not null) text = Replace(text, $"      timeout-minutes: {timeoutMinutes}\n",
            $"      timeout-minutes: {timeoutMinutes}\n      runs-on: {runsOn}\n");
        return text;
    }

    /// <summary>
    /// The gate workflow, as seeded; optionally with the gate's token replaced (TS-S9's invalid-token override), or lingering
    /// on merge groups after it has decided, so a lock can open while it still runs (TS-S17).
    /// </summary>
    public string Gate(string? token = null, int lingerSeconds = 0)
    {
        var uses = $"        uses: {GateRepo}/.github/actions/gate@main\n";
        var text = Read("templates/main-watcher-gate.yml").Replace(
            "Actium-Group-Corporation/MainWatcher/.github/actions/gate@v1", $"{GateRepo}/.github/actions/gate@main");
        if (token is not null) text = Replace(text, uses, $"{uses}        with:\n          github-token: {token}\n");
        if (lingerSeconds > 0)
            text = Replace(text, uses, $"{uses}      - name: sandbox-linger\n        if: github.event_name == 'merge_group'\n        run: sleep {lingerSeconds}\n");
        return text;
    }

    static string Replace(string text, string old, string replacement) =>
        text.Contains(old, StringComparison.Ordinal) ? text.Replace(old, replacement, StringComparison.Ordinal)
            : throw new InvalidDataException($"The template no longer contains \"{old.Trim()}\"; update the scenario suite.");

    /// <summary>
    /// Whether a file on a target matches what the template gives, ignoring line ends: a Windows checkout seeds CRLF, and
    /// the suite writes LF.
    /// </summary>
    public static bool Same(string? actual, string? expected) =>
        actual?.Replace("\r\n", "\n").TrimEnd() == expected?.Replace("\r\n", "\n").TrimEnd();

    /// <summary>Whether two sets of switches are the same, whatever their formatting.</summary>
    public static bool SameSwitches(string? actual, JsonObject expected)
    {
        if (actual is null) return false;
        try { return JsonNode.DeepEquals(JsonNode.Parse(actual), expected); }
        catch (JsonException) { return false; }
    }
}
