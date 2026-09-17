using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MainWatcher.Worker;

/// <summary>Runs a <see cref="TriggerCycle"/> every <c>check_period</c> (ADR-010), then reviews the health alerts (ADR-012).</summary>
public sealed class TriggerService(TriggerCycle cycle, GitHubAccess access, WorkerHealth health, WorkerAlerts alerts,
    WorkerSettings settings, IHostApplicationLifetime lifetime, ILogger<TriggerService> log) : BackgroundService
{
    /// <summary>The alert review's own budget, so a GitHub that stalls on an issue write cannot hold up the next cycle.</summary>
    public static readonly TimeSpan ReviewTimeout = TimeSpan.FromMinutes(2);

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
            CycleReport? report = null;
            string? failure = null;
            try
            {
                report = await cycle.Run(timeout.Token);
                log.LogInformation("Cycle finished: {Targets} targets, {Dispatched} dispatched, {Errors} errors.",
                    report.Result.Targets, report.Result.Dispatched, report.Result.Errors);
            }
            catch (WorkerConfigurationException e)
            {
                Stop(e);
                return;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (HttpRequestException e)
            {
                failure = $"the cycle failed: {e.Message}";
                log.LogError("Cycle failed: {Message}", e.Message);
            }
            catch (OperationCanceledException)
            {
                failure = $"the cycle was cancelled after {WorkerSettings.CycleTimeout}";
                log.LogError("Cycle cancelled after {Timeout}.", WorkerSettings.CycleTimeout);
            }
            catch (Exception e)
            {
                failure = $"the cycle failed: {e.Message}";
                log.LogError(e, "Cycle failed: {Message}", e.Message);
            }
            health.Finished(DateTimeOffset.UtcNow, report?.Result);
            await Review(report, failure, stopping);
        } while (await timer.WaitForNextTickAsync(stopping));
    }

    /// <summary>
    /// Judges the health conditions. It never throws: <see cref="WorkerAlerts"/> swallows a failed alert, and a stalled review
    /// is cut short so it cannot delay the next cycle. A cycle counts as failed when it threw or any target errored.
    /// </summary>
    async Task Review(CycleReport? report, string? failure, CancellationToken stopping)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        budget.CancelAfter(ReviewTimeout);
        try { await alerts.Review(report?.Observations, failure, budget.Token); }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            log.LogWarning("The health review was cancelled after {Timeout}.", ReviewTimeout);
        }
    }

    void Stop(WorkerConfigurationException e)
    {
        log.LogCritical("Configuration error: {Message}", e.Message);
        Environment.ExitCode = 2;
        lifetime.StopApplication();
    }
}
