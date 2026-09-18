namespace MainWatcher.Core;

/// <summary>Markdown the watcher writes into issues and check runs: escaping for text from targets, and commit links.</summary>
static class Markdown
{
    /// <summary>
    /// One line of plain text. HTML-encoding stops the text opening a hidden marker, <c>&amp;#64;</c> stops it mentioning
    /// anyone, and the backslashes stop it formatting.
    /// </summary>
    public static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text)
        .Replace("\r", " ").Replace("\n", " ").Replace("`", "\\`").Replace("*", "\\*").Replace("[", "\\[").Replace("@", "&#64;");

    /// <summary>How long a field taken from a target repository may be before it is clipped.</summary>
    public const int MaxFieldLength = 200;

    /// <summary>Clips a field to <see cref="MaxFieldLength"/>. Clipped before escaping, so the escaped result stays bounded.</summary>
    public static string Clip(string text) => text.Length > MaxFieldLength ? text[..MaxFieldLength] + "…" : text;

    /// <summary>A commit SHA abbreviated the way GitHub's UI abbreviates it.</summary>
    public static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>A link to a commit, labelled with its short SHA.</summary>
    public static string Commit(string repo, string sha) => $"[`{Short(sha)}`](https://github.com/{repo}/commit/{sha})";
}
