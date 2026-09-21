using System.Diagnostics;
using System.Text;

namespace MainWatcher.Scenarios.Infra;

/// <summary>Runs git, kubectl, docker and the sandbox's own shell scripts.</summary>
public static class Shell
{
    /// <summary>Runs a command and returns its standard output. A non-zero exit throws, with the end of both streams.</summary>
    public static async Task<string> Run(string file, IEnumerable<string> args, CancellationToken ct, string? directory = null,
        IDictionary<string, string>? env = null, string? input = null, Action<string>? progress = null)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            WorkingDirectory = directory ?? Environment.CurrentDirectory
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var (name, value) in env ?? new Dictionary<string, string>()) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{file} did not start");
        var output = new StringBuilder();
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (output) output.AppendLine(e.Data); progress?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (errors) errors.AppendLine(e.Data); progress?.Invoke(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{file} {string.Join(' ', args)} exited {process.ExitCode}: {Tail(errors.ToString())} {Tail(output.ToString())}".Trim());
        return output.ToString();
    }

    /// <summary>Bash, for the sandbox's scripts. On Windows this is Git Bash, found beside git.</summary>
    public static string Bash => bash ??= FindBash();
    static string? bash;

    static string FindBash()
    {
        if (!OperatingSystem.IsWindows()) return "bash";
        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("MW_BASH"),
                     @"C:\Program Files\Git\bin\bash.exe",
                     @"C:\Program Files (x86)\Git\bin\bash.exe"
                 })
            if (candidate is { Length: > 0 } && File.Exists(candidate)) return candidate;
        return "bash";
    }

    static string Tail(string text) => text.Length > 1500 ? "…" + text[^1500..] : text;
}
