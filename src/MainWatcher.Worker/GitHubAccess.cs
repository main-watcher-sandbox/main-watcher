using System.Collections.Concurrent;
using System.Net;
using MainWatcher.Core;
using Microsoft.Extensions.Logging;

namespace MainWatcher.Worker;

/// <summary>
/// The worker's two App identities and the gateways built from them (ADR-010). Each gateway authenticates with an installation
/// token scoped to the one repository it reads or dispatches in, so every GitHub call still goes through <see cref="GitHubGateway"/>.
/// It is also the worker's own health source: refused credentials and the rate-limit headers of every response (ADR-012, R-13).
/// </summary>
public sealed class GitHubAccess : IAccessHealth
{
    readonly WorkerSettings settings;
    readonly ILogger log;
    readonly ConcurrentDictionary<string, IGitHubGateway> targets = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string> refused = new(StringComparer.Ordinal);
    readonly RateLimitWatch rateLimits = new();
    readonly GitHubGateway observerApp;
    readonly GitHubGateway doorbellApp;

    /// <param name="transport">The inner handler, for tests. By default one pooled HTTPS connection pool.</param>
    /// <param name="delay">The gateways' retry delay, for tests.</param>
    public GitHubAccess(WorkerSettings settings, ILoggerFactory loggers, HttpMessageHandler? transport = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.settings = settings;
        log = loggers.CreateLogger<GitHubAccess>();
        // One watch under every client, so the lowest budget of any App and installation is the one the worker reports.
        rateLimits.InnerHandler = transport ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
        var gateways = loggers.CreateLogger<GitHubGateway>();

        GitHubGateway AppGateway(AppCredentials app) =>
            new(GitHubApp.Client(settings.Api, new GitHubAppJwtHandler(app.AppId, app.Key) { InnerHandler = rateLimits }, disposeHandler: false),
                settings.MainWatcherAppId, delay);
        IGitHubGateway Installation(GitHubGateway app, string name, string repo) =>
            new GitHubGateway(GitHubApp.Client(settings.Api,
                new InstallationTokenHandler(ct => Token(app, name, repo, ct)) { InnerHandler = rateLimits }, disposeHandler: false),
                settings.MainWatcherAppId, delay, message => gateways.LogWarning("{Message}", message));

        observerApp = AppGateway(settings.Observer);
        doorbellApp = AppGateway(settings.Doorbell);
        Watcher = Installation(observerApp, ObserverName, settings.WatcherRepo);
        Doorbell = Installation(doorbellApp, DoorbellName, settings.WatcherRepo);
        Target = repo => targets.GetOrAdd(repo, r => Installation(observerApp, ObserverName, r));
    }

    const string ObserverName = "mw-observer";
    const string DoorbellName = "mw-doorbell";

    /// <summary><c>mw-observer</c> on the watcher repo, for <c>targets.yml</c> and the <c>watch.yml</c> runs.</summary>
    public IGitHubGateway Watcher { get; }

    /// <summary><c>mw-doorbell</c> on the watcher repo, which starts <c>watch.yml</c> and raises the worker's alerts.</summary>
    public IGitHubGateway Doorbell { get; }

    /// <summary><c>mw-observer</c> on one target. One gateway per target, so its check-run snapshot is kept between cycles.</summary>
    public Func<string, IGitHubGateway> Target { get; }

    public IReadOnlyDictionary<string, string> TokenFailures => refused;

    public RateLimit? TakeLowestRateLimit() => rateLimits.Take();

    /// <summary>
    /// Mints a token for one App on one repository, and records a credential GitHub refuses so the worker can alert on it
    /// (ADR-012). A key revoked, or an App uninstalled, while the worker runs shows up here rather than only in the logs. The
    /// record is dropped as soon as the same credential works again, so the alert clears by itself.
    /// </summary>
    async Task<InstallationToken> Token(GitHubGateway app, string name, string repo, CancellationToken ct)
    {
        try
        {
            var token = await app.InstallationToken(repo, ct);
            refused.TryRemove($"{name} on {repo}", out _);
            return token;
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            refused[$"{name} on {repo}"] = $"HTTP {(int)e.StatusCode!}";
            throw;
        }
    }

    /// <summary>
    /// Mints one installation token per App before the first cycle. A rejected credential is a configuration error: the key is
    /// wrong, or the App is not installed on the watcher repo, and no cycle could ever do anything. A transient failure, including
    /// a GitHub that stalls past <see cref="WorkerSettings.VerifyTimeout"/>, is only logged, since the cycles retry.
    /// </summary>
    /// <param name="ct">The worker's stopping token. Each check gets its own shorter budget, linked to it.</param>
    public async Task Verify(CancellationToken ct)
    {
        foreach (var (name, app) in new[] { (ObserverName, observerApp), (DoorbellName, doorbellApp) })
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(settings.VerifyTimeout);
            try
            {
                await Token(app, name, settings.WatcherRepo, budget.Token);
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
