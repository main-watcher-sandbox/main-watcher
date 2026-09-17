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
builder.Services.AddHostedService<TriggerService>();

var app = builder.Build();
app.MapGet("/healthz", (WorkerHealth health) =>
{
    var now = DateTimeOffset.UtcNow;
    var body = new { live = health.IsLive(now), last_cycle = health.LastFinished, last_result = health.LastResult };
    return body.live ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});
// A rejected App credential is a configuration error: no cycle could ever do anything, so the worker must not look healthy.
try
{
    using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await app.Services.GetRequiredService<GitHubAccess>().Verify(startup.Token);
}
catch (WorkerConfigurationException e)
{
    Console.Error.WriteLine($"Configuration error: {e.Message}");
    return 2;
}

await app.RunAsync();
return Environment.ExitCode;
