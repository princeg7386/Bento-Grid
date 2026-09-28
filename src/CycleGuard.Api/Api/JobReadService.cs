using CycleGuard.Api.Configuration;
using CycleGuard.Api.Data;
using CycleGuard.Api.Domain;
using CycleGuard.Api.Downstream;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CycleGuard.Api.Api;

/// <summary>
/// Everything the dashboard reads. Risk is computed here rather than stored, because it is a
/// function of the clock: a job that was OnTrack ten seconds ago can be AtRisk now without
/// anything about the row changing.
/// </summary>
public sealed class JobReadService(
    IDbContextFactory<CycleGuardDbContext> dbContextFactory,
    TimeProvider timeProvider,
    IOptions<CycleGuardOptions> options,
    SimulationState simulation,
    OutageRegistry outages,
    RawErrorVault rawErrors)
{
    /// <summary>
    /// Ceiling on how many jobs are pulled into memory for risk evaluation. The demo runs
    /// 300; a real deployment would push the risk maths into SQL. Noted in the README limits.
    /// </summary>
    private const int MaxJobsScanned = 5000;

    private CycleGuardOptions Options => options.Value;

    public RiskContext BuildRiskContext(JobType type) => new(
        timeProvider.UtcNow(),
        Options.Risk.AtRiskThresholdMinutes,
        simulation.TimeScale,
        Options.Retry.PolicyFor(type));

    public async Task<IReadOnlyList<JobSummaryDto>> ListJobsAsync(
        string? state,
        string? risk,
        string? endpoint,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = dbContext.Jobs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(state) && Enum.TryParse<JobState>(state, true, out var parsedState))
        {
            query = query.Where(j => j.State == parsedState);
        }

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            query = query.Where(j => j.DownstreamEndpoint == endpoint);
        }

        var jobs = await query.Take(MaxJobsScanned).ToListAsync(cancellationToken);
        var summaries = jobs.Select(ToSummary).ToList();

        if (!string.IsNullOrWhiteSpace(risk) && Enum.TryParse<RiskLevel>(risk, true, out var parsedRisk))
        {
            summaries = summaries.Where(s => s.Risk == parsedRisk.ToString()).ToList();
        }

        return SortByTimeToBreach(summaries);
    }

    /// <summary>
    /// The default order, and the whole product thesis: soonest to cost money first.
    /// Finished work sinks to the bottom regardless of its deadline.
    /// </summary>
    public static IReadOnlyList<JobSummaryDto> SortByTimeToBreach(IEnumerable<JobSummaryDto> summaries)
        => summaries
            .OrderBy(s => s.Risk == nameof(RiskLevel.Done) ? 1 : 0)
            .ThenBy(s => s.SecondsToDeadline)
            .ThenBy(s => s.Id)
            .ToList();

    public async Task<JobDetailDto?> GetJobAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var job = await dbContext.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null)
        {
            return null;
        }

        var attempts = await dbContext.JobAttempts.AsNoTracking()
            .Where(a => a.JobId == id)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync(cancellationToken);

        var events = await dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.JobId == id)
            .OrderBy(e => e.AtTicks).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

        var exposeRaw = Options.Demo.ExposeRawErrors;

        var attemptDtos = attempts.Select(attempt =>
        {
            var raw = exposeRaw ? rawErrors.Get(attempt.Id) : null;
            return new AttemptDto(
                attempt.Id,
                attempt.AttemptNumber,
                attempt.WorkerId,
                Clock.ToUtc(attempt.StartedTicks),
                attempt.FinishedTicks is null ? null : Clock.ToUtc(attempt.FinishedTicks.Value),
                attempt.Outcome?.ToString(),
                attempt.FailureSignature,
                attempt.FailureClass?.ToString(),
                attempt.ErrorMasked,
                attempt.StackTraceMasked,
                attempt.BackoffSeconds,
                attempt.NextAttemptTicks is null ? null : Clock.ToUtc(attempt.NextAttemptTicks.Value),
                raw?.Message,
                raw?.StackTrace);
        }).ToList();

        var lastFailed = attemptDtos.LastOrDefault(a => a.ErrorMasked is not null);

        return new JobDetailDto(
            ToSummary(job),
            job.Payload,
            attemptDtos,
            BuildUpcomingBackoff(job),
            events.Select(e => new AuditEventDto(e.Id, Clock.ToUtc(e.AtTicks), e.Actor, e.EventType, e.Details)).ToList(),
            job.LastErrorMasked,
            job.LastStackTraceMasked,
            lastFailed?.ErrorRaw,
            lastFailed?.StackTraceRaw,
            exposeRaw);
    }

    /// <summary>The retry schedule still ahead, with the ones that land after the deadline flagged.</summary>
    public IReadOnlyList<BackoffSlotDto> BuildUpcomingBackoff(Job job)
    {
        if (job.IsTerminal || job.State == JobState.DeadLettered)
        {
            return [];
        }

        var policy = Options.Retry.PolicyFor(job.Type);
        var schedule = BackoffCalculator.UpcomingSchedule(policy, job.Attempts, job.MaxAttempts, job.Id);

        var cursor = job.NextAttemptTicks ?? timeProvider.UtcTicks();
        var slots = new List<BackoffSlotDto>();

        for (var index = 0; index < schedule.Count; index++)
        {
            var (attemptNumber, simulatedDelay) = schedule[index];
            var realDelay = simulation.ToRealSeconds(simulatedDelay);

            // The first slot is the retry already on the clock; later slots are projections
            // that assume every remaining attempt also fails.
            if (index > 0)
            {
                cursor += (long)(realDelay * TimeSpan.TicksPerSecond);
            }

            slots.Add(new BackoffSlotDto(
                attemptNumber,
                simulatedDelay,
                Math.Round(realDelay, 2),
                Clock.ToUtc(cursor),
                cursor > job.DeadlineTicks));
        }

        return slots;
    }

    public async Task<IReadOnlyList<DeadLetterGroupDto>> ListDeadLettersAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var jobs = await dbContext.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.DeadLettered)
            .Take(MaxJobsScanned)
            .ToListAsync(cancellationToken);

        return jobs
            .GroupBy(j => j.LastFailureSignature ?? FailureSignatures.Unknown)
            .Select(group =>
            {
                var explanation = CauseRules.Explain(group.Key);
                var summaries = SortByTimeToBreach(group.Select(ToSummary));
                return new DeadLetterGroupDto(
                    group.Key,
                    explanation.Cause,
                    explanation.SuggestedAction,
                    explanation.Class.ToString(),
                    summaries.Count,
                    summaries.Sum(s => s.AmountAtStakeCents),
                    summaries);
            })
            .OrderByDescending(g => g.DollarsAtRiskCents)
            .ToList();
    }

    public async Task<IReadOnlyList<GroupDto>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var jobs = await dbContext.Jobs.AsNoTracking()
            .Where(j => j.LastFailureSignature != null && j.State != JobState.Succeeded)
            .Take(MaxJobsScanned)
            .ToListAsync(cancellationToken);

        var nowTicks = timeProvider.UtcTicks();

        return jobs
            .GroupBy(j => new { j.DownstreamEndpoint, Signature = j.LastFailureSignature ?? FailureSignatures.Unknown })
            .Select(group =>
            {
                var explanation = CauseRules.Explain(group.Key.Signature);
                var summaries = group.Select(ToSummary).ToList();
                var earliest = group.Min(j => j.DeadlineTicks);

                return new GroupDto(
                    group.Key.DownstreamEndpoint,
                    group.Key.Signature,
                    explanation.Class.ToString(),
                    explanation.Cause,
                    explanation.SuggestedAction,
                    summaries.Count,
                    summaries.Where(s => RiskEvaluator.CountsAsAtRisk(Enum.Parse<RiskLevel>(s.Risk))).Sum(s => s.AmountAtStakeCents),
                    Clock.ToUtc(earliest),
                    (long)Math.Round((earliest - nowTicks) / (double)TimeSpan.TicksPerSecond),
                    summaries.Count(s => s.Risk == nameof(RiskLevel.Breached)),
                    summaries.Count(s => s.Risk == nameof(RiskLevel.NeedsHuman)),
                    // A group is healing when its jobs are still on the retry path rather than parked.
                    explanation.Class == FailureClass.Transient
                        && group.Any(j => j.State is JobState.RetryScheduled or JobState.Queued or JobState.Running));
            })
            .OrderByDescending(g => g.DollarsAtRiskCents)
            .ThenBy(g => g.SecondsToEarliestDeadline)
            .ToList();
    }

    public async Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var jobs = await dbContext.Jobs.AsNoTracking().Take(MaxJobsScanned).ToListAsync(cancellationToken);
        var summaries = jobs.Select(ToSummary).ToList();

        var nowTicks = timeProvider.UtcTicks();
        var nowUtc = timeProvider.UtcNow();

        var byState = summaries.GroupBy(s => s.State).ToDictionary(g => g.Key, g => g.Count());
        var byRisk = summaries.GroupBy(s => s.Risk).ToDictionary(g => g.Key, g => g.Count());

        foreach (var state in Enum.GetNames<JobState>())
        {
            byState.TryAdd(state, 0);
        }

        foreach (var risk in Enum.GetNames<RiskLevel>())
        {
            byRisk.TryAdd(risk, 0);
        }

        var atRisk = summaries.Where(s => RiskEvaluator.CountsAsAtRisk(Enum.Parse<RiskLevel>(s.Risk))).ToList();

        // Throughput over the trailing real minute, which is what "is it moving?" means.
        var windowStart = nowTicks - TimeSpan.TicksPerMinute;
        var completedInWindow = await dbContext.Jobs.AsNoTracking()
            .CountAsync(j => j.CompletedTicks != null && j.CompletedTicks >= windowStart, cancellationToken);

        var succeeded = byState.GetValueOrDefault(nameof(JobState.Succeeded));
        var deadLettered = byState.GetValueOrDefault(nameof(JobState.DeadLettered));
        var finished = succeeded + deadLettered;

        var attemptedJobs = jobs.Count(j => j.Attempts > 0);
        var totalAttempts = jobs.Sum(j => j.Attempts);
        var retryRate = attemptedJobs == 0 ? 0 : (totalAttempts - attemptedJobs) / (double)attemptedJobs * 100.0;

        var duplicates = await dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EventType == AuditEventTypes.DuplicatePrevented)
            .Select(e => e.JobId)
            .ToListAsync(cancellationToken);

        var duplicateCents = jobs.Where(j => duplicates.Contains(j.Id)).Sum(j => j.AmountAtStakeCents);

        var cycleClose = simulation.CycleCloseTicks;
        var secondsToClose = cycleClose == 0
            ? 0
            : (long)Math.Round((cycleClose - nowTicks) / (double)TimeSpan.TicksPerSecond);

        var activeOutages = outages.Active(nowTicks)
            .Select(pair => new OutageDto(
                pair.Key,
                Clock.ToUtc(pair.Value),
                (long)Math.Round((pair.Value - nowTicks) / (double)TimeSpan.TicksPerSecond)))
            .OrderBy(o => o.Endpoint)
            .ToList();

        return new StatusDto(
            simulation.HasScenario,
            cycleClose == 0 ? null : Clock.ToUtc(cycleClose),
            secondsToClose,
            (long)simulation.ToSimulatedSeconds(secondsToClose),
            simulation.TimeScale,
            summaries.Count,
            byState,
            byRisk,
            atRisk.Count,
            atRisk.Sum(s => s.AmountAtStakeCents),
            summaries.Where(s => s.Risk == nameof(RiskLevel.Done) && s.State == nameof(JobState.Succeeded)).Sum(s => s.AmountAtStakeCents),
            deadLettered,
            duplicates.Count,
            duplicateCents,
            completedInWindow,
            finished == 0 ? 0 : Math.Round(succeeded / (double)finished * 100.0, 1),
            Math.Round(retryRate, 1),
            Options.Workers.Count,
            activeOutages);
    }

    /// <summary>Project a job row plus the clock into what the list shows.</summary>
    public JobSummaryDto ToSummary(Job job)
    {
        var context = BuildRiskContext(job.Type);
        var assessment = RiskEvaluator.Evaluate(job, context);
        var explanation = job.LastFailureSignature is null ? null : CauseRules.Explain(job.LastFailureSignature);

        long? secondsToNext = job.NextAttemptTicks is null
            ? null
            : (long)Math.Round((job.NextAttemptTicks.Value - Clock.Ticks(context.NowUtc)) / (double)TimeSpan.TicksPerSecond);

        return new JobSummaryDto(
            job.Id,
            job.Type.ToString(),
            job.State.ToString(),
            assessment.Level.ToString(),
            assessment.ReasonCode,
            assessment.Reason,
            assessment.SecondsToDeadline,
            (long)simulation.ToSimulatedSeconds(assessment.SecondsToDeadline),
            job.DeadlineUtc,
            job.AmountAtStakeCents,
            job.Attempts,
            job.MaxAttempts,
            job.DownstreamEndpoint,
            secondsToNext,
            secondsToNext is null ? null : (long)simulation.ToSimulatedSeconds(secondsToNext.Value),
            job.LastFailureSignature,
            job.LastFailureClass?.ToString(),
            explanation?.Cause,
            explanation?.SuggestedAction,
            job.IdempotencyKey,
            job.RequeueCount,
            job.DeadLetterResolved,
            job.IsSynthetic);
    }
}
