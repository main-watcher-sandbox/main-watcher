using System.Collections.Concurrent;
using MainWatcher.Core;
using MainWatcher.Worker;

WorkerSettings settings;
try { settings = WorkerSettings.Load(Environment.GetEnvironmentVariable); }
catch (WorkerConfigurationException e)
{
    Console.Error.WriteLine($"Configuration error: {e.Message}");
    return 2;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders().AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ "; o.UseUtcTimestamp = true; })
    // Liveness probes would otherwise log several lines each.
    .AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// Only /healthz listens, for the liveness probe, on ASPNETCORE_HTTP_PORTS (8080 in the image). No Service or Ingress exposes it (ADR-010).

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new WorkerHealth(settings.CheckPeriod, DateTimeOffset.UtcNow));
builder.Services.AddSingleton(services =>
{
    // One connection pool; each client authenticates through its own handler, and every GitHub call goes through GitHubGateway.
    var sockets = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
    var github = services.GetRequiredService<ILoggerFactory>().CreateLogger<GitHubGateway>();
    GitHubGateway AppGateway(AppCredentials app) =>
        new(GitHubApp.Client(settings.Api, new GitHubAppJwtHandler(app.AppId, app.Key) { InnerHandler = sockets }, disposeHandler: false),
            settings.MainWatcherAppId);
    GitHubGateway Installation(GitHubGateway app, string repo) =>
        new(GitHubApp.Client(settings.Api, new InstallationTokenHandler(ct => app.InstallationToken(repo, ct)) { InnerHandler = sockets },
            disposeHandler: false), settings.MainWatcherAppId, log: message => github.LogWarning("{Message}", message));

    var observer = AppGateway(settings.Observer);
    var targets = new ConcurrentDictionary<string, IGitHubGateway>(StringComparer.OrdinalIgnoreCase);
    return new TriggerCycle(Installation(observer, settings.WatcherRepo), Installation(AppGateway(settings.Doorbell), settings.WatcherRepo),
        repo => targets.GetOrAdd(repo, r => Installation(observer, r)), settings.WatcherRepo, settings.TargetsPath, new WorkFinder(),
        services.GetRequiredService<ILogger<TriggerCycle>>());
});
builder.Services.AddHostedService<TriggerService>();

var app = builder.Build();
app.MapGet("/healthz", (WorkerHealth health) =>
{
    var now = DateTimeOffset.UtcNow;
    var body = new { live = health.IsLive(now), last_cycle = health.LastFinished, last_result = health.LastResult };
    return body.live ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});
await app.RunAsync();
return Environment.ExitCode;
