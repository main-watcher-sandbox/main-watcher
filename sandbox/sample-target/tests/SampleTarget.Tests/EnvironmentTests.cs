using System.Collections;
using System.Text.RegularExpressions;

namespace SampleTarget.Tests;

/// <summary>
/// <c>inspect_environment</c>: the test run looks through its own environment and finds no Main Watcher
/// credential (TS-S8, ARCH-001 §8). The <c>main-watcher</c> key lives only in the watcher repo's
/// <c>reporter</c> environment and the worker's keys only in the cluster, so a test run should hold neither,
/// nor any GitHub token: the reusable workflow gives the tests none (ADR-018).
/// </summary>
public partial class EnvironmentTests
{
    // A PEM private key, a GitHub token (installation, OAuth, user, refresh or fine-grained), or a git
    // extraheader left behind by a checkout with persisted credentials.
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----|\bgh[opsur]_[A-Za-z0-9]{36,}|\bgithub_pat_[A-Za-z0-9_]{22,}|extraheader\s*=\s*AUTHORIZATION:", RegexOptions.IgnoreCase)]
    private static partial Regex Credential();

    // The names Main Watcher gives its keys (watch.yml, the worker's Deployment), and any other private key.
    [GeneratedRegex(@"MAIN_WATCHER_PRIVATE_KEY|MW_OBSERVER_KEY|MW_DOORBELL_KEY|PRIVATE_KEY", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialName();

    // Package caches and toolchains: large, and written by restore rather than by anything holding a key.
    private static readonly string[] SkippedDirectories = [".nuget", ".dotnet", "node_modules", ".cargo", ".rustup", ".git/objects"];

    private const long LargestFile = 256 * 1024;

    [Fact]
    public void NoMainWatcherKey()
    {
        Assert.SkipUnless(SandboxSwitches.Current.InspectEnvironment, "inspect_environment is false");

        var findings = new List<string>();

        var variables = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Select(e => (Name: (string)e.Key, Value: e.Value as string ?? ""))
            .OrderBy(v => v.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var (name, value) in variables)
        {
            if (CredentialName().IsMatch(name) || Credential().IsMatch(value))
            {
                findings.Add($"environment variable {name}");
            }
        }

        var roots = Roots();
        var files = 0;
        foreach (var root in roots)
        {
            foreach (var file in Files(root))
            {
                files++;
                if (Credential().IsMatch(Read(file)))
                {
                    findings.Add($"file {file}");
                }
            }
        }

        Report(variables.Select(v => v.Name), roots, files, findings);
        Assert.True(findings.Count == 0, "Credentials found in the test run: " + string.Join("; ", findings));
    }

    /// <summary>
    /// On a runner: the runner's work folder, which holds this checkout, the downloaded actions and the step
    /// scripts, and the home folder, where git and other tools keep credentials. Elsewhere, only this repo.
    /// </summary>
    private static string[] Roots()
    {
        var workspace = Environment.GetEnvironmentVariable("RUNNER_WORKSPACE");
        if (string.IsNullOrEmpty(workspace))
        {
            return [Path.GetDirectoryName(SandboxSwitches.FilePath)!];
        }

        string?[] candidates =
        [
            Path.GetDirectoryName(workspace),
            Environment.GetEnvironmentVariable("RUNNER_TEMP"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];
        return candidates.OfType<string>().Where(Directory.Exists).Distinct().ToArray();
    }

    private static IEnumerable<string> Files(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        return Directory.EnumerateFiles(root, "*", options)
            .Where(f => !SkippedDirectories.Any(d => f.Replace('\\', '/').Contains($"/{d}/", StringComparison.Ordinal)))
            .Where(f =>
            {
                try { return new FileInfo(f).Length <= LargestFile; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            });
    }

    private static string Read(string file)
    {
        try { return File.ReadAllText(file); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    /// <summary>What was inspected, in the job summary when run in Actions, as the scenario's evidence.</summary>
    private static void Report(IEnumerable<string> names, string[] roots, int files, List<string> findings)
    {
        var lines = new List<string>
        {
            "### TS-S8: environment inspection",
            "",
            findings.Count == 0 ? "No Main Watcher key or GitHub token found." : $"**{findings.Count} credential(s) found:** {string.Join("; ", findings)}",
            "",
            $"- Files scanned: {files}, under {string.Join(", ", roots.Select(r => $"`{r}`"))}",
            $"- Environment variables (names only): {string.Join(", ", names.Select(n => $"`{n}`"))}",
            "",
        };
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, lines));

        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
        {
            File.AppendAllLines(summary, lines);
        }
    }
}
