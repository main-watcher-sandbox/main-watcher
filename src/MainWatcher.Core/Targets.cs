using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MainWatcher.Core;

/// <summary>A watched repository and its ADR-009 execution and scheduling settings.</summary>
public sealed class Target
{
    public const string DefaultTestCommand = "dotnet test --no-restore";
    public const string DefaultResultsGlob = "**/TestResults/*.ctrf.json";
    public const int DefaultTimeout = 30;

    public string Repo { get; set; } = "";
    public string TestCommand { get; set; } = DefaultTestCommand;
    public string ResultsGlob { get; set; } = DefaultResultsGlob;
    public int Timeout { get; set; } = DefaultTimeout;
    public int PollInterval { get; set; } = 15;
    public string[] Notify { get; set; } = [];
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <c>lock_lease</c> (ADR-014), from the file's one setting rather than this entry: how long a renewal keeps this target's
    /// open lock enforced.
    /// </summary>
    [YamlIgnore]
    public TimeSpan LockLease { get; set; } = Lease.Default;
}

/// <summary>Strict targets.yml schema; caller workflow settings are verified before dispatch.</summary>
public sealed class TargetConfiguration
{
    /// <summary>
    /// <c>lock_lease</c> in minutes, at most a day, since the gate rejects a lease more than 24 hours ahead. ADR-014 sets it
    /// once for every target: it is how long an unmaintained lock blocks merges, which is a property of the watcher's
    /// availability, not of any one repository. The sandbox shortens it to 10 minutes for TS-S7.
    /// </summary>
    public int LockLease { get; set; } = (int)Lease.Default.TotalMinutes;

    public List<Target> Targets { get; set; } = [];

    public static TargetConfiguration Parse(string yaml)
    {
        var config = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking().Build().Deserialize<TargetConfiguration>(yaml)
            ?? throw new InvalidDataException("Expected a targets mapping.");
        if (config.Targets is null) throw new InvalidDataException("targets must be an array.");
        if (config.LockLease is < 1 or > 1440)
            throw new InvalidDataException("lock_lease is 1 to 1440 minutes; the gate rejects a lease more than 24 hours ahead.");
        var repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in config.Targets)
        {
            if (target is null || target.Repo is null || !Regex.IsMatch(target.Repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
                || !repos.Add(target.Repo) || string.IsNullOrWhiteSpace(target.TestCommand)
                || string.IsNullOrWhiteSpace(target.ResultsGlob) || target.Timeout is < 1 or > 340
                || target.PollInterval < 1 || target.Notify is null || !target.Notify.All(n => n is not null && Mentions.IsHandle(n)))
                throw new InvalidDataException("Invalid or duplicate target; timeout is 1–340 minutes and poll_interval is positive minutes; notify holds user or org/team handles.");
            target.LockLease = TimeSpan.FromMinutes(config.LockLease);
        }
        return config;
    }
}
