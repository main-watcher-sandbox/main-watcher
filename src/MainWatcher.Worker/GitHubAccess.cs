using System.Collections.Concurrent;
using System.Net;
using MainWatcher.Core;
using Microsoft.Extensions.Logging;

namespace MainWatcher.Worker;

/// <summary>
/// The worker's two App identities and the gateways built from them (ADR-010). Each gateway authenticates with an installation
/// token scoped to the one repository it reads or dispatches in, so every GitHub call still goes through <see cref="GitHubGateway"/>.
/// </summary>
public sealed class GitHubAccess
{
    readonly WorkerSettings settings;
    readonly ILogger log;
    readonly ConcurrentDictionary<string, IGitHubGateway> targets = new(StringComparer.OrdinalIgnoreCase);
    readonly GitHubGateway observerApp;
    readonly GitHubGateway doorbellApp;

    /// <param name="transport">The inner handler, for tests. By default one pooled HTTPS connection pool.</param>
    /// <param name="delay">The gateways' retry delay, for tests.</param>
    public GitHubAccess(WorkerSettings settings, ILoggerFactory loggers, HttpMessageHandler? transport = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.settings = settings;
        log = loggers.CreateLogger<GitHubAccess>();
        var inner = transport ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
        var gateways = loggers.CreateLogger<GitHubGateway>();

        GitHubGateway AppGateway(AppCredentials app) =>
            new(GitHubApp.Client(settings.Api, new GitHubAppJwtHandler(app.AppId, app.Key) { InnerHandler = inner }, disposeHandler: false),
                settings.MainWatcherAppId, delay);
        IGitHubGateway Installation(GitHubGateway app, string repo) =>
            new GitHubGateway(GitHubApp.Client(settings.Api,
                new InstallationTokenHandler(ct => app.InstallationToken(repo, ct)) { InnerHandler = inner }, disposeHandler: false),
                settings.MainWatcherAppId, delay, message => gateways.LogWarning("{Message}", message));

        observerApp = AppGateway(settings.Observer);
        doorbellApp = AppGateway(settings.Doorbell);
        Watcher = Installation(observerApp, settings.WatcherRepo);
        Doorbell = Installation(doorbellApp, settings.WatcherRepo);
        Target = repo => targets.GetOrAdd(repo, r => Installation(observerApp, r));
    }

    /// <summary><c>mw-observer</c> on the watcher repo, for <c>targets.yml</c> and the <c>watch.yml</c> runs.</summary>
    public IGitHubGateway Watcher { get; }

    /// <summary><c>mw-doorbell</c> on the watcher repo, which starts <c>watch.yml</c>.</summary>
    public IGitHubGateway Doorbell { get; }

    /// <summary><c>mw-observer</c> on one target. One gateway per target, so its check-run snapshot is kept between cycles.</summary>
    public Func<string, IGitHubGateway> Target { get; }

    /// <summary>
    /// Mints one installation token per App before the first cycle. A rejected credential is a configuration error: the key is
    /// wrong, or the App is not installed on the watcher repo, and no cycle could ever do anything. A transient failure, including
    /// a GitHub that stalls past <see cref="WorkerSettings.VerifyTimeout"/>, is only logged, since the cycles retry.
    /// </summary>
    /// <param name="ct">The worker's stopping token. Each check gets its own shorter budget, linked to it.</param>
    public async Task Verify(CancellationToken ct)
    {
        foreach (var (name, app) in new[] { ("mw-observer", observerApp), ("mw-doorbell", doorbellApp) })
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(settings.VerifyTimeout);
            try
            {
                await app.InstallationToken(settings.WatcherRepo, budget.Token);
                log.LogInformation("{App} authenticated to {Repo}.", name, settings.WatcherRepo);
            }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                throw new WorkerConfigurationException(
                    $"{name} could not authenticate to {settings.WatcherRepo} (HTTP {(int)e.StatusCode!}): check its App ID, "
                    + "its private key, and that the App is installed on that repository.");
            }
            catch (Exception e) when (e is HttpRequestException or IOException
                || e is OperationCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning("{App} could not be verified now, so the cycles will try again: {Message}", name, e.Message);
            }
        }
    }
}
