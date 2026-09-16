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
}

/// <summary>Strict targets.yml schema; caller workflow settings are verified before dispatch.</summary>
public sealed class TargetConfiguration
{
    public List<Target> Targets { get; set; } = [];

    public static TargetConfiguration Parse(string yaml)
    {
        var config = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking().Build().Deserialize<TargetConfiguration>(yaml)
            ?? throw new InvalidDataException("Expected a targets mapping.");
        if (config.Targets is null) throw new InvalidDataException("targets must be an array.");
        var repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in config.Targets)
        {
            if (target is null || target.Repo is null || !Regex.IsMatch(target.Repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
                || !repos.Add(target.Repo) || string.IsNullOrWhiteSpace(target.TestCommand)
                || string.IsNullOrWhiteSpace(target.ResultsGlob) || target.Timeout is < 1 or > 340
                || target.PollInterval < 1 || target.Notify is null || !target.Notify.All(n => n is not null && Mentions.IsHandle(n)))
                throw new InvalidDataException("Invalid or duplicate target; timeout is 1–340 minutes and poll_interval is positive minutes; notify holds user or org/team handles.");
        }
        return config;
    }
}
