using CycleGuard.Api.Configuration;
using CycleGuard.Api.Queue;
using Microsoft.Extensions.Options;

namespace CycleGuard.Api.Workers;

/// <summary>
/// The pool of queue-draining workers. Each one claims the ready job with the nearest
/// deadline, runs it, and goes back for another. They share nothing but the database, so the
/// atomic claim is the only coordination they need.
/// </summary>
public sealed class WorkerPoolService(
    JobQueue queue,
    JobExecutor executor,
    TimeProvider timeProvider,
    IOptions<CycleGuardOptions> options,
    ILogger<WorkerPoolService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = Math.Max(1, options.Value.Workers.Count);
        logger.LogInformation("Starting {Count} CycleGuard workers.", workerCount);

        var workers = Enumerable
            .Range(1, workerCount)
            .Select(index => RunWorkerAsync($"worker-{index}", stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(string workerId, CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromMilliseconds(Math.Max(10, options.Value.Workers.PollIntervalMs));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await queue.TryClaimAsync(workerId, stoppingToken);
                if (job is null)
                {
                    // Task.Delay takes the TimeProvider so tests can advance time instead of waiting.
                    await Task.Delay(idleDelay, timeProvider, stoppingToken);
                    continue;
                }

                await executor.ExecuteAsync(job, workerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // One poisoned job must never take a worker out of service.
                logger.LogError(exception, "Worker {WorkerId} hit an unhandled error; continuing.", workerId);
                try
                {
                    await Task.Delay(idleDelay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Worker {WorkerId} stopped.", workerId);
    }
}
