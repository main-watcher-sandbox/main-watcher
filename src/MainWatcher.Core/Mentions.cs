using System.Text.RegularExpressions;

namespace MainWatcher.Core;

/// <summary>Who a lock issue mentions (CQ-6): the target's notify list, else the owners of CODEOWNERS' <c>*</c> rule.</summary>
public static class Mentions
{
    public static readonly string[] CodeOwnersPaths = [".github/CODEOWNERS", "CODEOWNERS", "docs/CODEOWNERS"];
    static readonly Regex Handle = new(@"^@?[A-Za-z0-9][A-Za-z0-9-]*(/[A-Za-z0-9_.-]+)?$");

    /// <summary>A user or org/team handle, with or without a leading <c>@</c>.</summary>
    public static bool IsHandle(string text) => Handle.IsMatch(text);

    public static string Normalize(string handle) => handle.StartsWith('@') ? handle : "@" + handle;

    /// <summary>The owners of the last <c>*</c> rule. Email owners cannot be mentioned and are skipped.</summary>
    public static IReadOnlyList<string> StarOwners(string codeOwners)
    {
        IReadOnlyList<string> owners = [];
        foreach (var line in codeOwners.Split('\n'))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens is not ["*", ..]) continue;
            // The last matching rule wins in CODEOWNERS, including one that clears the owners.
            owners = tokens.Skip(1).TakeWhile(t => !t.StartsWith('#'))
                .Where(t => t.StartsWith('@') && IsHandle(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return owners;
    }
}
