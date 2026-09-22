namespace MainWatcher.Scenarios.Infra;

/// <summary>
/// A timestamped log for the suite or one scenario: every line goes to the console, prefixed with its source, and to that
/// source's own file in the run's output folder.
/// </summary>
public sealed class Log(string source, string file)
{
    static readonly Lock Console = new();

    public string Source { get; } = source;

    public void Info(string message) => Write(" ", message);
    public void Warn(string message) => Write("!", message);

    void Write(string mark, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:HH:mm:ss}Z {mark} {message}";
        lock (Console)
        {
            System.Console.WriteLine($"[{Source}] {line}");
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }
}
