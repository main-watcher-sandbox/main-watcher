namespace MainWatcher.Core;

/// <summary>Escaping for text from targets (test output, pusher names, workflow step names) written into issues and check runs.</summary>
static class Markdown
{
    /// <summary>
    /// One line of plain text. HTML-encoding stops the text opening a hidden marker, <c>&amp;#64;</c> stops it mentioning
    /// anyone, and the backslashes stop it formatting.
    /// </summary>
    public static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text)
        .Replace("\r", " ").Replace("\n", " ").Replace("`", "\\`").Replace("*", "\\*").Replace("[", "\\[").Replace("@", "&#64;");
}
