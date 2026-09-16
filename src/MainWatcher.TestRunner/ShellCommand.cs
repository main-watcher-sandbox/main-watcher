using System.Diagnostics;

namespace MainWatcher.TestRunner;

/// <summary>Runs a command line through a shell, sharing this process's console.</summary>
public static class ShellCommand
{
    /// <summary>
    /// Returns the command's exit code, or null when <paramref name="deadline"/> was reached
    /// first. Then the whole process tree has been stopped.
    /// </summary>
    public static async Task<int?> RunAsync(string shell, string command, string workingDirectory, CancellationToken deadline)
    {
        var start = new ProcessStartInfo(shell) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {shell}");
        try
        {
            await process.WaitForExitAsync(deadline);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(30));
            return null;
        }
    }
}
