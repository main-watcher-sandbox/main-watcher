namespace MainWatcher.Core;

/// <summary>
/// The sandbox's fault-injection settings (TS-001 §3): replica variables such as <c>MW_SANDBOX_EXIT_AFTER</c>, and the trigger
/// worker's environment. Production never sets them. A setting is a comma-separated list. A bare entry applies to every
/// target; an entry written <c>owner/repo=value</c> applies to that target only, so the scenario suite can fault one target
/// while other scenarios run on the rest (MainWatcher#25).
/// </summary>
public static class SandboxSwitch
{
    /// <summary>The values <paramref name="setting"/> gives <paramref name="repo"/>, in order: its own entries and the bare ones.</summary>
    public static IReadOnlyList<string> For(string? setting, string repo) =>
        (setting ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('=', 2) is [var target, var value]
                ? target.Trim().Equals(repo, StringComparison.OrdinalIgnoreCase) ? value.Trim() : null
                : entry)
            .OfType<string>()
            .Where(value => value.Length > 0)
            .ToArray();
}
