using System.Security.Cryptography;

namespace MainWatcher.Worker.Tests;

public class SettingsTests
{
    static Dictionary<string, string?> Valid(string keyFile) => new()
    {
        ["MW_WATCHER_REPO"] = "owner/watcher",
        ["MW_MAIN_WATCHER_APP_ID"] = "1",
        ["MW_OBSERVER_APP_ID"] = "2",
        ["MW_OBSERVER_KEY_FILE"] = keyFile,
        ["MW_DOORBELL_APP_ID"] = "3",
        ["MW_DOORBELL_KEY_FILE"] = keyFile,
    };

    static string KeyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mw-worker-{Guid.NewGuid():N}.pem");
        using var rsa = RSA.Create(2048);
        File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
        return path;
    }

    [Fact]
    public void DefaultsApplyToAValidEnvironment()
    {
        var key = KeyFile();
        try
        {
            var settings = WorkerSettings.Load(Valid(key).GetValueOrDefault);
            Assert.Equal("owner/watcher", settings.WatcherRepo);
            Assert.Equal(1, settings.MainWatcherAppId);
            Assert.Equal("2", settings.Observer.AppId);
            Assert.Equal("3", settings.Doorbell.AppId);
            Assert.Equal(TimeSpan.FromSeconds(60), settings.CheckPeriod);
            Assert.Equal("targets.yml", settings.TargetsPath);
            Assert.Equal("https://api.github.com/", settings.Api.AbsoluteUri);
        }
        finally { File.Delete(key); }
    }

    [Fact]
    public void EveryMissingSettingIsReportedAtOnce()
    {
        var error = Assert.Throws<WorkerConfigurationException>(() => WorkerSettings.Load(_ => null));
        foreach (var name in new[] { "MW_WATCHER_REPO", "MW_MAIN_WATCHER_APP_ID", "MW_OBSERVER_APP_ID", "MW_OBSERVER_KEY_FILE", "MW_DOORBELL_APP_ID", "MW_DOORBELL_KEY_FILE" })
            Assert.Contains($"{name} is required.", error.Message);
    }

    // A whitespace-only value is not a setting: without this, MW_MAIN_WATCHER_APP_ID="   " read as App ID 0 and matched no check run.
    [Theory]
    [InlineData("MW_WATCHER_REPO")]
    [InlineData("MW_MAIN_WATCHER_APP_ID")]
    [InlineData("MW_OBSERVER_APP_ID")]
    [InlineData("MW_OBSERVER_KEY_FILE")]
    [InlineData("MW_DOORBELL_APP_ID")]
    [InlineData("MW_DOORBELL_KEY_FILE")]
    public void WhitespaceOnlyRequiredSettingsAreMissing(string name)
    {
        var key = KeyFile();
        try
        {
            var env = Valid(key);
            env[name] = "   ";
            Assert.Contains($"{name} is required.",
                Assert.Throws<WorkerConfigurationException>(() => WorkerSettings.Load(env.GetValueOrDefault)).Message);
        }
        finally { File.Delete(key); }
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmedFromAValidSetting()
    {
        var key = KeyFile();
        try
        {
            var env = Valid(key);
            env["MW_WATCHER_REPO"] = "  owner/watcher  ";
            env["MW_MAIN_WATCHER_APP_ID"] = " 4966469 ";
            var settings = WorkerSettings.Load(env.GetValueOrDefault);
            Assert.Equal("owner/watcher", settings.WatcherRepo);
            Assert.Equal(4966469, settings.MainWatcherAppId);
        }
        finally { File.Delete(key); }
    }

    // The sandbox queue deadline (TS-S16 (g)). Unset in production, and an unusable value falls back to the ADR-013 default
    // the same way in the watcher, so the two can never disagree about which runs are stale.
    [Theory]
    [InlineData(null, 30)]
    [InlineData("10", 10)]
    [InlineData("45", 30)]
    [InlineData("later", 30)]
    public void TheQueueDeadlineComesFromTheEnvironment(string? value, int minutes)
    {
        var key = KeyFile();
        try
        {
            var env = Valid(key);
            if (value is not null) env["MW_QUEUE_DEADLINE_MINUTES"] = value;
            Assert.Equal(TimeSpan.FromMinutes(minutes), WorkerSettings.Load(env.GetValueOrDefault).QueueDeadline);
        }
        finally { File.Delete(key); }
    }

    [Theory]
    [InlineData("MW_WATCHER_REPO", "watcher")]
    [InlineData("MW_MAIN_WATCHER_APP_ID", "-1")]
    [InlineData("MW_OBSERVER_APP_ID", "abc")]
    [InlineData("MW_DOORBELL_KEY_FILE", "does-not-exist.pem")]
    [InlineData("MW_OBSERVER_KEY_FILE", "NOT_A_KEY")]
    [InlineData("MW_CHECK_PERIOD_SECONDS", "5")]
    [InlineData("MW_CHECK_PERIOD_SECONDS", "1m")]
    [InlineData("MW_GITHUB_API_URL", "api.github.com")]
    public void InvalidSettingsFailFast(string name, string value)
    {
        var key = KeyFile();
        var notKey = Path.GetTempFileName();
        try
        {
            File.WriteAllText(notKey, "not a key");
            var env = Valid(key);
            env[name] = value == "NOT_A_KEY" ? notKey : value;
            Assert.Contains(name, Assert.Throws<WorkerConfigurationException>(() => WorkerSettings.Load(env.GetValueOrDefault)).Message);
        }
        finally
        {
            File.Delete(key);
            File.Delete(notKey);
        }
    }
}
