using YamlDotNet.RepresentationModel;

namespace MainWatcher.Core;

/// <summary>Verifies the three execution settings owned by the target's reusable caller (ADR-009).</summary>
public static class CallerConfiguration
{
    public static void Validate(Target target, string yaml)
    {
        var document = new YamlStream();
        document.Load(new StringReader(yaml));
        if (document.Documents.Count != 1 || document.Documents[0].RootNode is not YamlMappingNode root
            || !root.Children.TryGetValue(new YamlScalarNode("jobs"), out var node) || node is not YamlMappingNode jobs)
            throw new InvalidDataException("Target caller must contain jobs.");
        var callers = jobs.Children.Values.OfType<YamlMappingNode>().Where(j =>
            Scalar(j, "uses")?.Split('@')[0].EndsWith("/.github/workflows/run-integration-tests.yml", StringComparison.Ordinal) == true).ToArray();
        if (callers.Length != 1) throw new InvalidDataException("Expected exactly one reusable test workflow caller.");
        var inputs = callers[0].Children.TryGetValue(new YamlScalarNode("with"), out var with) && with is YamlMappingNode mapping
            ? mapping : new YamlMappingNode();
        var command = Scalar(inputs, "test-command") ?? "dotnet test --no-restore";
        var glob = Scalar(inputs, "results-glob") ?? "**/TestResults/*.ctrf.json";
        var timeout = Scalar(inputs, "timeout-minutes") ?? "30";
        if (command != target.TestCommand || glob != target.ResultsGlob
            || !int.TryParse(timeout, out var minutes) || minutes != target.Timeout)
            throw new InvalidDataException($"{target.Repo}: caller test-command, results-glob and timeout-minutes must match targets.yml using literal values.");
    }

    static string? Scalar(YamlMappingNode mapping, string key)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out var value)) return null;
        return value is YamlScalarNode scalar && scalar.Value is not null ? scalar.Value
            : throw new InvalidDataException($"Caller {key} must be a literal scalar.");
    }
}
