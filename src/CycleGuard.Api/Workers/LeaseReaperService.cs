using CycleGuard.Api.Configuration;
using CycleGuard.Api.Queue;
using Microsoft.Extensions.Options;

namespace CycleGuard.Api.Workers;

/// <summary>
/// Returns jobs whose worker stopped reporting back to the queue. Without this a crash
/// mid-payment-run would leave the job stuck in Running forever, which on a payment cycle is
/// the difference between "late" and "never".
/// </summary>
public sealed class LeaseReaperService(
    JobQueue queue,
    TimeProvider timeProvider,
    IOptions<CycleGuardOptions> options,
    ILogger<LeaseReaperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, options.Value.Workers.LeaseReaperIntervalMs));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
                await queue.ReclaimExpiredLeasesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Lease reaper pass failed; will retry on the next interval.");
            }
        }
    }
}
