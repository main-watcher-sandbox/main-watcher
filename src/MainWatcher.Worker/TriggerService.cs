using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MainWatcher.Worker;

/// <summary>Runs a <see cref="TriggerCycle"/> every <c>check_period</c> (ADR-010).</summary>
public sealed class TriggerService(TriggerCycle cycle, GitHubAccess access, WorkerHealth health, WorkerSettings settings,
    IHostApplicationLifetime lifetime, ILogger<TriggerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        // Here rather than before the host starts, so /healthz is already serving while GitHub is asked about the two Apps.
        try { await access.Verify(stopping); }
        catch (WorkerConfigurationException e)
        {
            Stop(e);
            return;
        }
        using var timer = new PeriodicTimer(settings.CheckPeriod);
        do
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(WorkerSettings.CycleTimeout);
            CycleResult? result = null;
            try
            {
                result = await cycle.Run(timeout.Token);
                log.LogInformation("Cycle finished: {Targets} targets, {Dispatched} dispatched, {Errors} errors.",
                    result.Targets, result.Dispatched, result.Errors);
            }
            catch (WorkerConfigurationException e)
            {
                Stop(e);
                return;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (HttpRequestException e)
            {
                log.LogError("Cycle failed: {Message}", e.Message);
            }
            catch (OperationCanceledException)
            {
                log.LogError("Cycle cancelled after {Timeout}.", WorkerSettings.CycleTimeout);
            }
            catch (Exception e)
            {
                log.LogError(e, "Cycle failed: {Message}", e.Message);
            }
            health.Finished(DateTimeOffset.UtcNow, result);
        } while (await timer.WaitForNextTickAsync(stopping));
    }

    void Stop(WorkerConfigurationException e)
    {
        log.LogCritical("Configuration error: {Message}", e.Message);
        Environment.ExitCode = 2;
        lifetime.StopApplication();
    }
}
