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
builder.Services.AddSingleton(services => new GitHubAccess(settings, services.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(services =>
{
    var access = services.GetRequiredService<GitHubAccess>();
    return new TriggerCycle(access.Watcher, access.Doorbell, access.Target, settings.WatcherRepo, settings.TargetsPath,
        new WorkFinder(), services.GetRequiredService<ILogger<TriggerCycle>>());
});
// mw-doorbell holds Issues: write on the watcher repo, where the watcher-infra alerts live (ADR-012).
builder.Services.AddSingleton(services =>
{
    var access = services.GetRequiredService<GitHubAccess>();
    return new WorkerAlerts(new Alerts(access.Doorbell, settings.WatcherRepo), access,
        services.GetRequiredService<ILogger<WorkerAlerts>>(), DateTimeOffset.UtcNow);
});
builder.Services.AddHostedService<TriggerService>();

var app = builder.Build();
app.MapGet("/healthz", (WorkerHealth health) =>
{
    var now = DateTimeOffset.UtcNow;
    var body = new { live = health.IsLive(now), last_cycle = health.LastFinished, last_result = health.LastResult };
    return body.live ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});
// TriggerService verifies both App credentials before its first cycle and stops the worker with code 2 if GitHub rejects one.
await app.RunAsync();
return Environment.ExitCode;
