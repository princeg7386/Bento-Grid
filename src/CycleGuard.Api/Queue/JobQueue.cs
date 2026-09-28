using System.Data.Common;
using CycleGuard.Api.Configuration;
using CycleGuard.Api.Data;
using CycleGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CycleGuard.Api.Queue;

/// <summary>
/// Every state change a job can undergo, expressed so that concurrent workers cannot
/// corrupt each other. Two rules run through all of it:
///
///  * The claim is one statement (UPDATE ... WHERE id = (SELECT ...) RETURNING *). There is
///    no read-then-write anywhere on the hot path, so two workers can never hold one job.
///  * "Now" is always passed in as a parameter from TimeProvider. No SQL datetime('now'),
///    which keeps the whole engine testable with FakeTimeProvider.
/// </summary>
public sealed class JobQueue(
    IDbContextFactory<CycleGuardDbContext> dbContextFactory,
    TimeProvider timeProvider,
    IOptions<CycleGuardOptions> options,
    SimulationState simulation,
    ILogger<JobQueue> logger)
{
    private CycleGuardOptions Options => options.Value;

    /// <summary>Column list for RETURNING, in the order <see cref="MapJob"/> expects.</summary>
    private const string JobColumns = """
        Id, Type, Payload, State, Attempts, MaxAttempts, DeadlineTicks, AmountAtStakeCents,
        IdempotencyKey, DownstreamIdempotencyKey, DownstreamEndpoint, NextAttemptTicks,
        LeaseExpiresTicks, ClaimedBy, LastFailureSignature, LastFailureClass, LastErrorMasked,
        LastStackTraceMasked, DeadLetterResolved, RequeueCount, CreatedTicks, UpdatedTicks,
        CompletedTicks, IsSynthetic
        """;

    // ---------------------------------------------------------------- enqueue

    public async Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken cancellationToken = default)
    {
        var allowedEndpoints = JobTypeCatalog.EndpointsFor(request.Type);
        var endpoint = request.DownstreamEndpoint ?? JobTypeCatalog.DefaultEndpointFor(request.Type);
        if (!allowedEndpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
        {
            return EnqueueResult.Invalid(
                $"Job type {request.Type} cannot target endpoint '{endpoint}'. Allowed: {string.Join(", ", allowedEndpoints)}.");
        }

        if (request.AmountAtStakeCents < 0)
        {
            return EnqueueResult.Invalid("AmountAtStakeCents cannot be negative.");
        }

        var nowTicks = timeProvider.UtcTicks();
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? $"{request.Type}-{Guid.NewGuid():N}"
            : request.IdempotencyKey.Trim();

        var job = new Job
        {
            Type = request.Type,
            Payload = string.IsNullOrWhiteSpace(request.Payload) ? "{}" : request.Payload,
            State = JobState.Queued,
            Attempts = 0,
            MaxAttempts = request.MaxAttempts ?? Options.Retry.MaxAttemptsFor(request.Type),
            DeadlineTicks = Clock.Ticks(request.DeadlineUtc),
            AmountAtStakeCents = request.AmountAtStakeCents,
            IdempotencyKey = idempotencyKey,
            DownstreamIdempotencyKey = string.IsNullOrWhiteSpace(request.DownstreamIdempotencyKey)
                ? idempotencyKey
                : request.DownstreamIdempotencyKey.Trim(),
            DownstreamEndpoint = endpoint,
            NextAttemptTicks = nowTicks,
            CreatedTicks = nowTicks,
            UpdatedTicks = nowTicks,
            IsSynthetic = request.IsSynthetic
        };

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.Jobs.Add(job);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return EnqueueResult.Duplicate(idempotencyKey);
        }

        // The audit row carries the job id, which only exists once the insert has run.
        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = job.Id,
            AtTicks = nowTicks,
            Actor = "system",
            EventType = AuditEventTypes.Enqueued,
            Details = $"Enqueued {request.Type} for {endpoint}, {request.AmountAtStakeCents} cents at stake."
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return EnqueueResult.Ok(job);
    }

    // ---------------------------------------------------------------- claim

    /// <summary>
    /// Atomically take the ready job with the nearest deadline. Earliest-deadline-first is
    /// the whole scheduling thesis: the oldest job is rarely the expensive one.
    ///
    /// This is deliberately a single statement. An earlier read-then-write version handed the
    /// same job to two workers under load (docs/WHAT_BROKE.md).
    /// </summary>
    public async Task<Job?> TryClaimAsync(string workerId, CancellationToken cancellationToken = default)
    {
        var nowTicks = timeProvider.UtcTicks();
        var leaseExpiry = nowTicks + (long)(Options.Workers.LeaseSeconds * TimeSpan.TicksPerSecond);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenAsync(dbContext, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE Jobs
            SET State = 'Running',
                Attempts = Attempts + 1,
                ClaimedBy = $worker,
                LeaseExpiresTicks = $leaseExpiry,
                NextAttemptTicks = NULL,
                UpdatedTicks = $now
            WHERE Id = (
                SELECT Id FROM Jobs
                WHERE State IN ('Queued', 'RetryScheduled')
                  AND (NextAttemptTicks IS NULL OR NextAttemptTicks <= $now)
                  AND Attempts < MaxAttempts
                ORDER BY DeadlineTicks ASC, Id ASC
                LIMIT 1
            )
            RETURNING {JobColumns};
            """;

        AddParameter(command, "$worker", workerId);
        AddParameter(command, "$leaseExpiry", leaseExpiry);
        AddParameter(command, "$now", nowTicks);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapJob(reader);
    }

    /// <summary>Open the attempt row for a freshly claimed job. Only the claim holder gets here.</summary>
    public async Task<JobAttempt> RecordAttemptStartAsync(Job job, string workerId, CancellationToken cancellationToken = default)
    {
        var nowTicks = timeProvider.UtcTicks();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var attempt = new JobAttempt
        {
            JobId = job.Id,
            AttemptNumber = job.Attempts,
            WorkerId = workerId,
            StartedTicks = nowTicks
        };

        dbContext.JobAttempts.Add(attempt);
        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = job.Id,
            AtTicks = nowTicks,
            Actor = workerId,
            EventType = AuditEventTypes.Claimed,
            Details = $"Attempt {job.Attempts} of {job.MaxAttempts} claimed."
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    // ---------------------------------------------------------------- completion

    /// <summary>
    /// Commit a successful attempt. The downstream ledger write and the job state change
    /// happen in one transaction, so there is no window where money moved but the job still
    /// looks unpaid, or the reverse.
    /// </summary>
    public async Task<CompletionResult> MarkSucceededAsync(
        Job job,
        long attemptId,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        var nowTicks = timeProvider.UtcTicks();
        JobStateMachine.EnsureCanTransition(JobState.Running, JobState.Succeeded, job.Id);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // ON CONFLICT DO NOTHING plus rows-affected is how "already done downstream" is
        // detected. Zero rows means this effect was already recorded under the same key.
        var ledgerRows = await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO DownstreamLedger (IdempotencyKey, Endpoint, JobId, AmountCents, CreatedTicks)
            VALUES (@p0, @p1, @p2, @p3, @p4)
            ON CONFLICT(IdempotencyKey) DO NOTHING;
            """,
            [job.DownstreamIdempotencyKey, job.DownstreamEndpoint, job.Id, job.AmountAtStakeCents, nowTicks],
            cancellationToken);

        var duplicatePrevented = ledgerRows == 0;

        // Only the worker still holding the lease may complete the job. If the lease was
        // reaped while this attempt was in flight, the update matches nothing and we roll back.
        var jobRows = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE Jobs
            SET State = 'Succeeded',
                CompletedTicks = @p0,
                UpdatedTicks = @p0,
                LeaseExpiresTicks = NULL,
                NextAttemptTicks = NULL
            WHERE Id = @p1 AND State = 'Running' AND ClaimedBy = @p2;
            """,
            [nowTicks, job.Id, workerId],
            cancellationToken);

        if (jobRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogWarning(
                "Job {JobId} could not be completed by {WorkerId}: the lease was no longer held.",
                job.Id,
                workerId);
            return new CompletionResult(false, false, "Lease no longer held.");
        }

        await FinishAttemptAsync(dbContext, attemptId, nowTicks, AttemptOutcome.Succeeded, null, null, null, null, null, cancellationToken);

        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = job.Id,
            AtTicks = nowTicks,
            Actor = workerId,
            EventType = duplicatePrevented ? AuditEventTypes.DuplicatePrevented : AuditEventTypes.DisbursementRecorded,
            Details = duplicatePrevented
                ? $"Downstream already had key '{job.DownstreamIdempotencyKey}'. No second effect was applied."
                : $"Recorded {job.AmountAtStakeCents} cents against '{job.DownstreamIdempotencyKey}' at {job.DownstreamEndpoint}."
        });

        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = job.Id,
            AtTicks = nowTicks,
            Actor = workerId,
            EventType = AuditEventTypes.Succeeded,
            Details = $"Succeeded on attempt {job.Attempts}."
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CompletionResult(true, duplicatePrevented, null);
    }

    /// <summary>Schedule the next attempt with exponential backoff, compressed by the demo time scale.</summary>
    public async Task<int> ScheduleRetryAsync(
        Job job,
        long attemptId,
        string workerId,
        ExecutionOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        JobStateMachine.EnsureCanTransition(JobState.Running, JobState.RetryScheduled, job.Id);

        var nowTicks = timeProvider.UtcTicks();
        var policy = Options.Retry.PolicyFor(job.Type);

        // The policy answers in simulated seconds, which is what the analyst reads. The
        // clock only has to wait the compressed real equivalent.
        var simulatedDelay = BackoffCalculator.DelaySecondsWithJitter(policy, job.Attempts, job.Id);
        var realDelaySeconds = simulation.ToRealSeconds(simulatedDelay);
        var nextTicks = nowTicks + (long)(realDelaySeconds * TimeSpan.TicksPerSecond);

        var maskedMessage = PhiMasker.Mask(outcome.RawMessage);
        var maskedStack = PhiMasker.Mask(outcome.RawStackTrace);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var rows = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE Jobs
            SET State = 'RetryScheduled',
                NextAttemptTicks = @p0,
                LeaseExpiresTicks = NULL,
                UpdatedTicks = @p1,
                LastFailureSignature = @p2,
                LastFailureClass = @p3,
                LastErrorMasked = @p4,
                LastStackTraceMasked = @p5
            WHERE Id = @p6 AND State = 'Running' AND ClaimedBy = @p7;
            """,
            [
                nextTicks, nowTicks, outcome.Signature ?? FailureSignatures.Unknown,
                FailureClass.Transient.ToString(), maskedMessage ?? string.Empty, maskedStack ?? string.Empty,
                job.Id, workerId
            ],
            cancellationToken);

        if (rows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogWarning("Job {JobId} retry not scheduled by {WorkerId}: lease no longer held.", job.Id, workerId);
            return 0;
        }

        await FinishAttemptAsync(
            dbContext, attemptId, nowTicks, AttemptOutcome.TransientFailure, outcome.Signature,
            FailureClass.Transient, maskedMessage, maskedStack, (simulatedDelay, nextTicks), cancellationToken);

        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = job.Id,
            AtTicks = nowTicks,
            Actor = workerId,
            EventType = AuditEventTypes.RetryScheduled,
            Details = $"Attempt {job.Attempts} failed ({outcome.Signature}). Retry in {simulatedDelay}s of cycle time."
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return simulatedDelay;
    }

    /// <summary>Send a job to the dead-letter table, either permanently failed or out of retries.</summary>
    public async Task DeadLetterAsync(
        Job job,
        long attemptId,
        string workerId,
        ExecutionOutcome outcome,
        string reason,
        CancellationToken cancellationToken = default)
    {
        JobStateMachine.EnsureCanTransition(JobState.Running, JobState.DeadLettered, job.Id);

        var nowTicks = timeProvider.UtcTicks();
        var failureClass = outcome.Class ?? FailureClass.Transient;
        var maskedMessage = PhiMasker.Mask(outcome.RawMessage);
        var maskedStack = PhiMasker.Mask(outcome.RawStackTrace);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var rows = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE Jobs
            SET State = 'DeadLettered',
                NextAttemptTicks = NULL,
                LeaseExpiresTicks = NULL,
                DeadLetterResolved = 0,
                UpdatedTicks = @p0,
                LastFailureSignature = @p1,
                LastFailureClass = @p2,
                LastErrorMasked = @p3,
                LastStackTraceMasked = @p4
            WHERE Id = @p5 AND State = 'Running' AND ClaimedBy = @p6;
            """,
            [
                nowTicks, outcome.Signature ?? FailureSignatures.Unknown, failureClass.ToString(),
                maskedMessage ?? string.Empty, maskedStack ?? string.Empty, job.Id, workerId
            ],
            cancellationToken);

        if (rows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogWarning("Job {JobId} not dead-lettered by {WorkerId}: lease no longer held.", job.Id, workerId);
            return;
        }

        await FinishAttemptAsync(
            dbContext, attemptId, nowTicks,
            failureClass == FailureClass.Permanent ? AttemptOutcome.PermanentFailure : AttemptOutcome.TransientFailure,
            outcome.Signature, failureClass, maskedMessage, maskedStack, null, cancellationToken);

        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = job.Id,
            AtTicks = nowTicks,
            Actor = workerId,
            EventType = AuditEventTypes.DeadLettered,
            Details = $"{reason} Signature: {outcome.Signature ?? FailureSignatures.Unknown}."
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- lease recovery

    /// <summary>
    /// Hand back every job whose worker stopped reporting. This is what makes a killed
    /// worker mid-payment-run recoverable: the lease lapses and another worker takes it,
    /// with the attempt history intact.
    /// </summary>
    public async Task<IReadOnlyList<ReclaimedJob>> ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        var nowTicks = timeProvider.UtcTicks();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenAsync(dbContext, cancellationToken);

        var reclaimed = new List<ReclaimedJob>();

        await using (var command = connection.CreateCommand())
        {
            // ClaimedBy is intentionally left in place so the audit trail can name the worker
            // that dropped the job; LeaseExpiresTicks going NULL is what releases it.
            command.CommandText = """
                UPDATE Jobs
                SET State = 'Queued',
                    LeaseExpiresTicks = NULL,
                    NextAttemptTicks = $now,
                    UpdatedTicks = $now
                WHERE State = 'Running'
                  AND LeaseExpiresTicks IS NOT NULL
                  AND LeaseExpiresTicks <= $now
                RETURNING Id, Attempts, ClaimedBy;
                """;
            AddParameter(command, "$now", nowTicks);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                reclaimed.Add(new ReclaimedJob(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        if (reclaimed.Count == 0)
        {
            return reclaimed;
        }

        foreach (var item in reclaimed)
        {
            var attempt = await dbContext.JobAttempts
                .Where(a => a.JobId == item.JobId && a.AttemptNumber == item.AttemptNumber)
                .FirstOrDefaultAsync(cancellationToken);

            if (attempt is not null && attempt.FinishedTicks is null)
            {
                attempt.FinishedTicks = nowTicks;
                attempt.Outcome = AttemptOutcome.LeaseExpired;
                attempt.FailureSignature = FailureSignatures.LeaseExpired;
                attempt.FailureClass = FailureClass.Transient;
                attempt.ErrorMasked = "Worker stopped reporting; lease expired and the job was returned to the queue.";
            }

            dbContext.AuditEvents.Add(new AuditEvent
            {
                JobId = item.JobId,
                AtTicks = nowTicks,
                Actor = "lease-reaper",
                EventType = AuditEventTypes.LeaseExpired,
                Details = $"Lease held by '{item.PreviousWorker ?? "unknown"}' expired on attempt {item.AttemptNumber}. Job requeued."
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Reclaimed {Count} job(s) with expired leases.", reclaimed.Count);
        return reclaimed;
    }

    // ---------------------------------------------------------------- requeue

    /// <summary>
    /// Analyst-driven requeue out of the dead-letter table. Safety comes from three things:
    /// the conditional WHERE State = 'DeadLettered' (so concurrent requests produce exactly
    /// one winner), the preserved idempotency key (so the downstream ledger refuses a second
    /// effect), and the preserved attempt history.
    /// </summary>
    public async Task<RequeueResult> RequeueAsync(
        long jobId,
        string analyst,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(analyst) || string.IsNullOrWhiteSpace(note))
        {
            return new RequeueResult(RequeueOutcome.MissingAnalystDetails, null);
        }

        var nowTicks = timeProvider.UtcTicks();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // One more attempt is granted, so a requeued job is not instantly dead again on the
        // exhausted-retries check. History is never rewritten: Attempts keeps counting up.
        var rows = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE Jobs
            SET State = 'Queued',
                NextAttemptTicks = @p0,
                LeaseExpiresTicks = NULL,
                DeadLetterResolved = 1,
                RequeueCount = RequeueCount + 1,
                MaxAttempts = Attempts + 1,
                UpdatedTicks = @p0
            WHERE Id = @p1 AND State = 'DeadLettered';
            """,
            [nowTicks, jobId],
            cancellationToken);

        if (rows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);

            var exists = await dbContext.Jobs.AsNoTracking().AnyAsync(j => j.Id == jobId, cancellationToken);
            if (!exists)
            {
                return new RequeueResult(RequeueOutcome.NotFound, null);
            }

            // Losing a requeue race is a normal outcome, not an error. It is still audited.
            await using var loserContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            loserContext.AuditEvents.Add(new AuditEvent
            {
                JobId = jobId,
                AtTicks = nowTicks,
                Actor = analyst,
                EventType = AuditEventTypes.RequeueRejected,
                Details = "Requeue rejected: the job was no longer dead-lettered. Another request won the race."
            });
            await loserContext.SaveChangesAsync(cancellationToken);

            var current = await dbContext.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
            return new RequeueResult(RequeueOutcome.NotDeadLettered, current);
        }

        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = jobId,
            AtTicks = nowTicks,
            Actor = analyst,
            EventType = AuditEventTypes.Requeued,
            Details = $"Requeued from dead-letter. Note: {PhiMasker.Mask(note)}"
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var job = await dbContext.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        return new RequeueResult(RequeueOutcome.Requeued, job);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task FinishAttemptAsync(
        CycleGuardDbContext dbContext,
        long attemptId,
        long nowTicks,
        AttemptOutcome outcome,
        string? signature,
        FailureClass? failureClass,
        string? maskedMessage,
        string? maskedStack,
        (int SimulatedDelaySeconds, long NextTicks)? backoff,
        CancellationToken cancellationToken)
    {
        var attempt = await dbContext.JobAttempts.FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
        if (attempt is null)
        {
            return;
        }

        attempt.FinishedTicks = nowTicks;
        attempt.Outcome = outcome;
        attempt.FailureSignature = signature;
        attempt.FailureClass = failureClass;
        attempt.ErrorMasked = maskedMessage;
        attempt.StackTraceMasked = maskedStack;

        if (backoff is { } schedule)
        {
            attempt.BackoffSeconds = schedule.SimulatedDelaySeconds;
            attempt.NextAttemptTicks = schedule.NextTicks;
        }
    }

    private static async Task<DbConnection> OpenAsync(CycleGuardDbContext dbContext, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        return connection;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Maps a RETURNING row in <see cref="JobColumns"/> order.</summary>
    private static Job MapJob(DbDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Type = Enum.Parse<JobType>(reader.GetString(1)),
        Payload = reader.GetString(2),
        State = Enum.Parse<JobState>(reader.GetString(3)),
        Attempts = reader.GetInt32(4),
        MaxAttempts = reader.GetInt32(5),
        DeadlineTicks = reader.GetInt64(6),
        AmountAtStakeCents = reader.GetInt64(7),
        IdempotencyKey = reader.GetString(8),
        DownstreamIdempotencyKey = reader.GetString(9),
        DownstreamEndpoint = reader.GetString(10),
        NextAttemptTicks = reader.IsDBNull(11) ? null : reader.GetInt64(11),
        LeaseExpiresTicks = reader.IsDBNull(12) ? null : reader.GetInt64(12),
        ClaimedBy = reader.IsDBNull(13) ? null : reader.GetString(13),
        LastFailureSignature = reader.IsDBNull(14) ? null : reader.GetString(14),
        LastFailureClass = reader.IsDBNull(15) ? null : Enum.Parse<FailureClass>(reader.GetString(15)),
        LastErrorMasked = reader.IsDBNull(16) ? null : reader.GetString(16),
        LastStackTraceMasked = reader.IsDBNull(17) ? null : reader.GetString(17),
        DeadLetterResolved = !reader.IsDBNull(18) && reader.GetInt64(18) != 0,
        RequeueCount = reader.GetInt32(19),
        CreatedTicks = reader.GetInt64(20),
        UpdatedTicks = reader.GetInt64(21),
        CompletedTicks = reader.IsDBNull(22) ? null : reader.GetInt64(22),
        IsSynthetic = !reader.IsDBNull(23) && reader.GetInt64(23) != 0
    };
}
