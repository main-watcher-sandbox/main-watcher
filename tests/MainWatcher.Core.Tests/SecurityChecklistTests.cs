using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace MainWatcher.Core.Tests;

/// <summary>
/// The workflow and deployment review checklist (TS-001 §6), checked against the committed files, so a change that
/// breaks an item fails CI instead of waiting for a reviewer to notice. What only the live sandbox can show — which
/// repositories each App is installed on, who can read the worker's Secret, what each token is refused — is
/// <c>sandbox/ts-s8-credential-scope.sh</c>.
/// </summary>
public partial class SecurityChecklistTests
{
    static readonly string Root = FindRoot();

    // Every workflow this repo runs or hands to a target, and the sandbox target's own.
    static IEnumerable<string> Workflows() =>
        Glob(".github/workflows", "*.yml").Concat(Glob("templates", "*.yml")).Concat(Glob("sandbox/sample-target/.github/workflows", "*.yml"));

    // Everything that can name an action: the workflows, the composite actions, and the steps publish-public.sh
    // inserts into the sandbox build of the test workflow.
    static IEnumerable<string> ActionUsers() =>
        Workflows().Concat(Directory.EnumerateFiles(Path.Combine(Root, ".github/actions"), "action.yml", SearchOption.AllDirectories))
            .Append(Path.Combine(Root, "sandbox/upload-switches.yml"));

    [Fact]
    public void NoWorkflowRunsOnPullRequestTarget()
    {
        foreach (var file in Workflows())
        {
            var on = Load(file)["on"];
            var triggers = on switch
            {
                YamlScalarNode s => [s.Value!],
                YamlSequenceNode s => s.Children.Select(c => ((YamlScalarNode)c).Value!),
                YamlMappingNode m => m.Children.Keys.Select(k => ((YamlScalarNode)k).Value!),
                _ => throw new InvalidDataException($"{file}: unreadable 'on'"),
            };
            Assert.DoesNotContain("pull_request_target", triggers);
        }
    }

    /// <summary>
    /// The test workflow runs target code with the target's secrets, so it must never hold an App credential: a
    /// token it minted would be readable by the tests (ARCH-001 §8).
    /// </summary>
    [Fact]
    public void ReusableTestWorkflowRequestsNoAppToken()
    {
        string[] files =
        [
            Path.Combine(Root, ".github/workflows/run-integration-tests.yml"),
            Path.Combine(Root, ".github/actions/test-runner/action.yml"),
            Path.Combine(Root, "sandbox/upload-switches.yml"),
        ];
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("create-github-app-token", text);
            Assert.DoesNotContain("access_tokens", text);
            Assert.DoesNotMatch(AppCredential(), text);
            Assert.DoesNotMatch(MainWatcherSecret(), text);
        }
    }

    /// <summary>
    /// <c>watch.yml</c> holds the main App key, so it checks out only the watcher repo at its own commit and calls
    /// no other workflow: target code runs only in the target's own run (ARCH-001 §8).
    /// </summary>
    [Fact]
    public void WatchRunsNoTargetCode()
    {
        var jobs = (YamlMappingNode)Load(Path.Combine(Root, ".github/workflows/watch.yml"))["jobs"];
        var checkouts = 0;
        foreach (var (name, node) in jobs.Children)
        {
            var job = (YamlMappingNode)node;
            Assert.False(job.Children.ContainsKey("uses"), $"watch.yml job {name} calls another workflow");
            foreach (var step in ((YamlSequenceNode)job["steps"]).Children.Cast<YamlMappingNode>())
            {
                if (!step.Children.TryGetValue("uses", out var uses) || !((YamlScalarNode)uses).Value!.StartsWith("actions/checkout@"))
                {
                    continue;
                }

                checkouts++;
                var with = step.Children.TryGetValue("with", out var w) ? (YamlMappingNode)w : new YamlMappingNode();
                var keys = with.Children.Keys.Select(k => ((YamlScalarNode)k).Value).ToList();
                Assert.DoesNotContain("repository", keys);
                Assert.DoesNotContain("ref", keys);
                Assert.Equal("false", ((YamlScalarNode)with["persist-credentials"]).Value);
            }
        }

        Assert.True(checkouts > 0, "watch.yml no longer checks out the watcher; update this test");
    }

    /// <summary>
    /// Anything not from this repo (or its public sandbox copy) is pinned to a commit SHA, so a moved tag cannot change
    /// what runs beside a token (R-16).
    /// </summary>
    [Fact]
    public void ThirdPartyActionsArePinnedBySha()
    {
        var unpinned = new List<string>();
        foreach (var file in ActionUsers())
        {
            foreach (var uses in Scalars(LoadNode(file), "uses"))
            {
                if (!Pinned(uses))
                {
                    unpinned.Add($"{Path.GetRelativePath(Root, file)}: {uses}");
                }
            }
        }

        Assert.Empty(unpinned);
    }

    [Theory]
    [InlineData("./.github/actions/gate", true)]
    [InlineData("Actium-Group-Corporation/MainWatcher/.github/actions/gate@v1", true)]
    [InlineData("main-watcher-sandbox/gate/.github/workflows/run-integration-tests.yml@main", true)]
    [InlineData("ctrf-io/github-test-reporter@7974087018bf4857cf5a9d78723e152038c3fa31", true)]
    [InlineData("actions/cache/restore@0057852bfaa89a56745cba8c7296529d2fc39830", true)]
    [InlineData("actions/checkout@v7", false)]
    [InlineData("ctrf-io/github-test-reporter@7974087", false)]
    [InlineData("someone/Actium-Group-Corporation/MainWatcher@v1", false)]
    [InlineData("docker://alpine:3", false)]
    public void OnlyThisRepoMayUseATag(string uses, bool allowed) => Assert.Equal(allowed, Pinned(uses));

    /// <summary>
    /// The <c>report</c> job runs a third-party action, so it references no secret and its token can only read
    /// (ARCH-001 §8, R-16).
    /// </summary>
    [Fact]
    public void ReportJobIsSecretFreeAndReadOnly()
    {
        var jobs = (YamlMappingNode)Load(Path.Combine(Root, ".github/workflows/run-integration-tests.yml"))["jobs"];
        var report = (YamlMappingNode)jobs["report"];

        var permissions = ((YamlMappingNode)report["permissions"]).Children
            .ToDictionary(p => ((YamlScalarNode)p.Key).Value!, p => ((YamlScalarNode)p.Value).Value!);
        Assert.Equal(new Dictionary<string, string> { ["actions"] = "read", ["contents"] = "read" }, permissions);

        Assert.False(report.Children.ContainsKey("secrets"));
        Assert.DoesNotContain(AllScalars(report), s => SecretReference().IsMatch(s));
    }

    /// <summary>The worker needs no inbound network access: no Service, Ingress or host port exposes it (ARCH-001 §10).</summary>
    [Fact]
    public void WorkerHasNoInboundServiceOrIngress()
    {
        string[] inbound = ["Service", "Ingress", "IngressClass", "Gateway", "HTTPRoute", "GRPCRoute", "TCPRoute", "TLSRoute"];
        foreach (var (file, document) in DeployDocuments())
        {
            if (document.Children.TryGetValue("kind", out var kind))
            {
                Assert.DoesNotContain(((YamlScalarNode)kind).Value, inbound);
            }

            var keys = AllKeys(document).ToList();
            Assert.DoesNotContain("hostPort", keys);
            Assert.DoesNotContain("hostNetwork", keys);
            Assert.DoesNotContain("nodePort", keys);
            // A kustomize patch is YAML inside a string: check its text too.
            Assert.DoesNotMatch(InboundText(), File.ReadAllText(file));
        }
    }

    /// <summary>
    /// Only the worker's pod reads the key Secret, as a mounted volume; nothing in the manifests grants any identity
    /// access to Secrets through the API, and the pod gets no service account token to try with. Who else can read
    /// the Secret in a live cluster is checked by <c>sandbox/ts-s8-credential-scope.sh</c>.
    /// </summary>
    [Fact]
    public void WorkerSecretHasRestrictedAccess()
    {
        string[] rbac = ["Role", "ClusterRole", "RoleBinding", "ClusterRoleBinding", "ServiceAccount"];
        var deployments = 0;
        foreach (var (_, document) in DeployDocuments())
        {
            var kind = document.Children.TryGetValue("kind", out var k) ? ((YamlScalarNode)k).Value : null;
            Assert.DoesNotContain(kind, rbac);
            if (kind != "Deployment")
            {
                continue;
            }

            deployments++;
            var pod = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)document["spec"])["template"])["spec"];
            Assert.Equal("false", ((YamlScalarNode)pod["automountServiceAccountToken"]).Value);
            Assert.False(pod.Children.ContainsKey("serviceAccountName"), "the worker's pod runs under a named service account; check its RBAC");

            var volume = ((YamlSequenceNode)pod["volumes"]).Children.Cast<YamlMappingNode>()
                .Single(v => ((YamlScalarNode)v["name"]).Value == "keys");
            var secret = (YamlMappingNode)volume["secret"];
            Assert.Equal("trigger-worker-keys", ((YamlScalarNode)secret["secretName"]).Value);
            // Owner and group read only; fsGroup makes the group the worker's own.
            Assert.Equal("0440", ((YamlScalarNode)secret["defaultMode"]).Value);
        }

        Assert.Equal(1, deployments);
    }

    static bool Pinned(string uses) =>
        uses.StartsWith("./", StringComparison.Ordinal)
        || uses.StartsWith("Actium-Group-Corporation/MainWatcher/", StringComparison.Ordinal)
        || uses.StartsWith("main-watcher-sandbox/gate/", StringComparison.Ordinal)
        || ShaPinned().IsMatch(uses);

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}$")]
    private static partial Regex ShaPinned();

    [GeneratedRegex(@"^\s*(app-id|client-id|private-key)\s*:", RegexOptions.Multiline)]
    private static partial Regex AppCredential();

    [GeneratedRegex(@"MAIN_WATCHER_PRIVATE_KEY|MW_OBSERVER|MW_DOORBELL")]
    private static partial Regex MainWatcherSecret();

    [GeneratedRegex(@"\bsecrets\b")]
    private static partial Regex SecretReference();

    [GeneratedRegex(@"kind:\s*(Service|Ingress)\b|hostPort|hostNetwork|nodePort")]
    private static partial Regex InboundText();

    static IEnumerable<(string File, YamlMappingNode Document)> DeployDocuments()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Root, "deploy"), "*.yaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "deploy"), "*.yml", SearchOption.AllDirectories))
            .ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var stream = new YamlStream();
            using var reader = new StringReader(File.ReadAllText(file));
            stream.Load(reader);
            foreach (var document in stream.Documents)
            {
                yield return (file, (YamlMappingNode)document.RootNode);
            }
        }
    }

    static YamlMappingNode Load(string file) => (YamlMappingNode)LoadNode(file);

    static YamlNode LoadNode(string file)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(file));
        stream.Load(reader);
        return stream.Documents.Single().RootNode;
    }

    static IEnumerable<string> Glob(string directory, string pattern)
    {
        var path = Path.Combine(Root, directory);
        return Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern) : [];
    }

    // The values of every mapping entry named key, at any depth.
    static IEnumerable<string> Scalars(YamlNode node, string key) => node switch
    {
        YamlMappingNode m => m.Children.SelectMany(c =>
            (c.Key is YamlScalarNode { Value: var k } && k == key && c.Value is YamlScalarNode { Value: { } v } ? [v] : Enumerable.Empty<string>())
            .Concat(Scalars(c.Value, key))),
        YamlSequenceNode s => s.Children.SelectMany(c => Scalars(c, key)),
        _ => [],
    };

    static IEnumerable<string> AllScalars(YamlNode node) => node switch
    {
        YamlScalarNode { Value: { } v } => [v],
        YamlMappingNode m => m.Children.SelectMany(c => AllScalars(c.Key).Concat(AllScalars(c.Value))),
        YamlSequenceNode s => s.Children.SelectMany(AllScalars),
        _ => [],
    };

    static IEnumerable<string> AllKeys(YamlNode node) => node switch
    {
        YamlMappingNode m => m.Children.SelectMany(c => (c.Key is YamlScalarNode { Value: { } k } ? [k] : Enumerable.Empty<string>()).Concat(AllKeys(c.Value))),
        YamlSequenceNode s => s.Children.SelectMany(AllKeys),
        _ => [],
    };

    // The repo root, found from the test binary: the checklist is about the committed tree, in this repo and its
    // sandbox replica alike.
    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MainWatcher.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException($"MainWatcher.slnx not found above {AppContext.BaseDirectory}");
    }
}
