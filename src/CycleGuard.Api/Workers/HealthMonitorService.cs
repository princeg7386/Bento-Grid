using CycleGuard.Api.Api;
using CycleGuard.Api.Configuration;
using CycleGuard.Api.Data;
using CycleGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CycleGuard.Api.Workers;

/// <summary>
/// The unattended monitor: nobody has to open the dashboard or run a query to know whether
/// last night is fine. This runs on its own, on a fixed real-world interval, and leaves one
/// answer in <see cref="MorningReportStore"/> for <c>GET /api/morning-report</c> to hand back
/// instantly -- the request never does the work itself.
///
/// The interval is deliberately real time, not the demo's compressed clock: an operator
/// checking this at 8:45am does not care that the queue is replaying a night at 20x speed.
/// </summary>
public sealed class HealthMonitorService(
    JobReadService reads,
    MorningReportStore store,
    IDbContextFactory<CycleGuardDbContext> dbContextFactory,
    TimeProvider timeProvider,
    IOptions<CycleGuardOptions> options,
    ILogger<HealthMonitorService> logger) : BackgroundService
{
    private string? _lastVerdict;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.Monitor.IntervalSeconds));

        // Compute one report immediately, so the endpoint is never empty for a full interval
        // right after startup.
        await CheckOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
                await CheckOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A failed check must never take the monitor itself out of service -- that
                // would turn "nobody has to check" into "and now nobody can".
                logger.LogError(exception, "Health check failed; will retry on the next interval.");
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        var status = await reads.GetStatusAsync(cancellationToken);
        var groups = await reads.GetGroupsAsync(cancellationToken);

        var report = MorningReportEvaluator.Evaluate(
            status,
            groups,
            timeProvider.UtcNow(),
            (int)Math.Max(5, options.Value.Monitor.IntervalSeconds));

        store.Set(report);

        if (_lastVerdict is { } previous && previous != report.Verdict)
        {
            await RecordVerdictChangeAsync(previous, report, cancellationToken);
        }


        _lastVerdict = report.Verdict;
    }

    /// <summary>
    /// Every state change in CycleGuard is audited (see AuditEvent); the monitor's own
    /// verdict is no exception, so "when did this turn Critical" is answerable later without
    /// having polled the endpoint at exactly the right moment.
    /// </summary>
    private async Task RecordVerdictChangeAsync(
        string previous,
        MorningReportDto report,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = null,
            AtTicks = timeProvider.UtcTicks(),
            Actor = "health-monitor",
            EventType = AuditEventTypes.MonitorVerdictChanged,
            Details = $"{previous} -> {report.Verdict}: {report.Headline}"
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Losing one audit row must not stop the monitor from serving the next report.
            logger.LogWarning(exception, "Could not record the monitor's verdict change.");
        }
    }
}
