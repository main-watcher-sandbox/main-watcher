using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MainWatcher.Core;

namespace MainWatcher.Worker;

/// <summary>A worker setting that is missing or invalid. The worker exits at once rather than running without it.</summary>
public sealed class WorkerConfigurationException(string message) : Exception(message);

/// <summary>An App the worker authenticates as: its ID and private key.</summary>
public sealed record AppCredentials(string AppId, RSA Key);

/// <summary>The worker's environment (ADR-010). <see cref="Load"/> reports every problem at once.</summary>
public sealed record WorkerSettings(
    string WatcherRepo,
    long MainWatcherAppId,
    AppCredentials Observer,
    AppCredentials Doorbell,
    TimeSpan CheckPeriod,
    string TargetsPath,
    Uri Api)
{
    public const string WatchWorkflow = "watch.yml";

    /// <summary>A cycle that runs longer is cancelled, so one stuck request cannot stop the worker.</summary>
    public static readonly TimeSpan CycleTimeout = TimeSpan.FromMinutes(5);

    public static WorkerSettings Load(Func<string, string?> env)
    {
        var errors = new List<string>();
        string Required(string name)
        {
            if (env(name) is { Length: > 0 } value) return value.Trim();
            errors.Add($"{name} is required.");
            return "";
        }
        long Id(string name)
        {
            var text = Required(name);
            if (text.Length == 0) return 0;
            if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) return id;
            errors.Add($"{name} must be a positive integer App ID.");
            return 0;
        }
        AppCredentials App(string prefix)
        {
            var id = Id($"MW_{prefix}_APP_ID");
            var path = Required($"MW_{prefix}_KEY_FILE");
            var key = RSA.Create();
            if (path.Length > 0)
            {
                try { key.ImportFromPem(File.ReadAllText(path)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
                {
                    errors.Add($"MW_{prefix}_KEY_FILE must be a readable PEM private key ({e.GetType().Name}).");
                }
            }
            return new(id.ToString(CultureInfo.InvariantCulture), key);
        }

        var watcherRepo = Required("MW_WATCHER_REPO");
        if (watcherRepo.Length > 0 && !Regex.IsMatch(watcherRepo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            errors.Add("MW_WATCHER_REPO must be owner/repo.");
        var mainWatcher = Id("MW_MAIN_WATCHER_APP_ID");
        var observer = App("OBSERVER");
        var doorbell = App("DOORBELL");
        var period = Seconds(env("MW_CHECK_PERIOD_SECONDS"), 60, errors);
        var targets = env("MW_TARGETS_PATH") is { Length: > 0 } path ? path : "targets.yml";
        if (!Uri.TryCreate(env("MW_GITHUB_API_URL") is { Length: > 0 } url ? url : GitHubApp.DefaultApi.AbsoluteUri, UriKind.Absolute, out var api)
            || api.Scheme is not ("https" or "http"))
            errors.Add("MW_GITHUB_API_URL must be an absolute http(s) URL.");
        else if (!api.AbsoluteUri.EndsWith('/')) api = new(api.AbsoluteUri + "/");
        if (errors.Count > 0) throw new WorkerConfigurationException(string.Join(" ", errors));
        return new(watcherRepo, mainWatcher, observer, doorbell, period, targets, api!);
    }

    static TimeSpan Seconds(string? text, int fallback, List<string> errors)
    {
        if (text is not { Length: > 0 }) return TimeSpan.FromSeconds(fallback);
        // Below 10 s, the worker would spend its installation rate limit for no faster detection than poll_interval allows.
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 10 and <= 3600)
            return TimeSpan.FromSeconds(seconds);
        errors.Add("MW_CHECK_PERIOD_SECONDS must be a whole number of seconds from 10 to 3600.");
        return TimeSpan.FromSeconds(fallback);
    }
}
