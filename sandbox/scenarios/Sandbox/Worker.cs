using MainWatcher.Scenarios.Infra;

namespace MainWatcher.Scenarios.Sandbox;

/// <summary>The sandbox trigger worker, in the cluster's <c>main-watcher-sandbox</c> namespace (docs/worker.md).</summary>
public sealed class Worker(string ns)
{
    public const string Deployment = "deploy/trigger-worker";

    public string Namespace { get; } = ns;

    Task<string> Kubectl(CancellationToken ct, params string[] args) => Shell.Run("kubectl", ["-n", Namespace, .. args], ct);

    public async Task Scale(int replicas, CancellationToken ct)
    {
        await Kubectl(ct, "scale", Deployment, $"--replicas={replicas}");
        if (replicas > 0) await Ready(ct);
        else await Poll.True("the trigger worker's pods to stop", TimeSpan.FromMinutes(3), async () =>
            (await Kubectl(ct, "get", "pods", "-l", "app.kubernetes.io/name=trigger-worker", "-o", "name")).Trim().Length == 0, ct,
            TimeSpan.FromSeconds(5));
    }

    /// <summary>Waits for the rollout, then for both Apps to have authenticated, which the worker logs before its first cycle.</summary>
    public async Task Ready(CancellationToken ct)
    {
        await Kubectl(ct, "rollout", "status", Deployment, "--timeout=180s");
        await Poll.True("the trigger worker to authenticate both Apps", TimeSpan.FromMinutes(3), async () =>
        {
            string log;
            try { log = await Kubectl(ct, "logs", Deployment, "--tail=200"); }
            catch (InvalidOperationException) { return false; }
            return log.Contains("mw-observer authenticated", StringComparison.Ordinal)
                && log.Contains("mw-doorbell authenticated", StringComparison.Ordinal);
        }, ct, TimeSpan.FromSeconds(5));
    }

    public async Task<int> Replicas(CancellationToken ct) =>
        int.TryParse((await Kubectl(ct, "get", Deployment, "-o", "jsonpath={.spec.replicas}")).Trim(), out var n) ? n : 0;

    public Task<string> Logs(TimeSpan since, CancellationToken ct) => Kubectl(ct, "logs", Deployment, $"--since={(int)since.TotalSeconds}s");
}
