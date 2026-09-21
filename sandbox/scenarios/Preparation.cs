using MainWatcher.Scenarios.Infra;
using MainWatcher.Scenarios.Sandbox;

namespace MainWatcher.Scenarios;

/// <summary>
/// Puts the commit under test into the sandbox (sandbox/README.md): the gate and test workflow to the public gate repo, the
/// tree to the watcher replica, the worker image to the cluster and the template to every target; then the suite's target
/// list and switches, and every target reset to green.
/// </summary>
public sealed class Preparation(SandboxOrg sandbox, string root, Log log)
{
    public const string ReplicaUrl = "https://github.com/main-watcher-sandbox/main-watcher.git";

    public async Task Deploy(string commit, CancellationToken ct)
    {
        log.Info("Publishing the gate, the test workflow and its variants to main-watcher-sandbox/gate.");
        await Shell.Run(Shell.Bash, ["sandbox/publish-public.sh"], ct, root);

        log.Info("Taking the tree under test to the watcher replica.");
        await Shell.Run("git", ["fetch", "-q", ReplicaUrl, "main"], ct, root);
        var take = (await Shell.Run("git", ["commit-tree", "HEAD^{tree}", "-p", "FETCH_HEAD", "-p", "HEAD",
            "-m", $"Sandbox: take MainWatcher at {commit[..7]} (scenario suite)"], ct, root)).Trim();
        await Shell.Run("git", ["push", "-q", ReplicaUrl, $"{take}:main"], ct, root);

        log.Info("Building the worker image and rolling it out.");
        await Shell.Run("docker", ["build", "--file", "src/MainWatcher.Worker/Dockerfile", "--tag", "main-watcher-worker:dev", "."], ct, root);
        await Shell.Run("kubectl", ["apply", "-k", "deploy/worker/sandbox"], ct, root);
        await Shell.Run("kubectl", ["-n", sandbox.Worker.Namespace, "scale", Worker.Deployment, "--replicas=1"], ct, root);
        await Shell.Run("kubectl", ["-n", sandbox.Worker.Namespace, "rollout", "restart", Worker.Deployment], ct, root);
        await sandbox.Worker.Ready(ct);

        log.Info($"Seeding {sandbox.Pool.Count} targets from the template.");
        using var seeding = new SemaphoreSlim(3);
        await Task.WhenAll(sandbox.Pool.Select(async target =>
        {
            await seeding.WaitAsync(ct);
            try { await Shell.Run(Shell.Bash, ["sandbox/seed-target.sh", target.Repo], ct, root); }
            finally { seeding.Release(); }
        }));
    }

    /// <summary>The replica's target list and switches, then every target reset. Needs the worker running.</summary>
    public async Task Configure(CancellationToken ct)
    {
        var deadline = $"{sandbox.QueueDeadlineTarget.Repo}={SandboxOrg.ShortQueueDeadline}";
        var overlay = await File.ReadAllTextAsync(Path.Combine(root, "deploy/worker/sandbox/kustomization.yaml"), ct);
        if (!overlay.Contains($"MW_QUEUE_DEADLINE_MINUTES={deadline}", StringComparison.Ordinal))
            throw new InvalidOperationException($"deploy/worker/sandbox gives the worker another queue deadline setting than {deadline}; " +
                "the worker and watch.yml must agree (TS-S16 (g)).");

        log.Info("Writing the replica's targets.yml and switches.");
        await sandbox.Replica.WriteTargets(sandbox.Pool.Select(sandbox.DefaultEntry), SandboxOrg.LockLease, ct);
        foreach (var name in Replica.Switches.Where(n => n != Replica.QueueDeadline)) await sandbox.Replica.DeleteVariable(name, ct);
        await sandbox.Replica.SetVariable(Replica.QueueDeadline, deadline, ct);
        await sandbox.Replica.EnableWatch(ct);
        if (await sandbox.Worker.Replicas(ct) == 0) await sandbox.Worker.Scale(1, ct);

        log.Info("Closing open alerts and resetting every target.");
        foreach (var alert in await sandbox.Replica.Alerts(ct))
        {
            await sandbox.GitHub.Post($"repos/{sandbox.Replica.Repo}/issues/{alert.Number}/comments",
                new { body = "Closed by the scenario suite before a run." }, ct);
            await sandbox.GitHub.Patch($"repos/{sandbox.Replica.Repo}/issues/{alert.Number}", new { state = "closed" }, ct);
        }
        await Task.WhenAll(sandbox.Pool.Select(async target =>
        {
            var targetLog = new Log(target.Name, Path.Combine(RunInfo.OutDir, $"{target.Name}.log"));
            try { await sandbox.Reset(target, targetLog, ct); }
            catch (ScenarioFailure e)
            {
                // A target the Apps cannot see is never tested, so its reset waits for a result that never comes.
                throw new ScenarioFailure($"{target.Repo} could not be reset: {e.Message}. Check that main-watcher and mw-observer " +
                    "are installed on it (sandbox/README.md, \"The scenario suite\").");
            }
            targetLog.Info("ready");
        }));
    }
}
