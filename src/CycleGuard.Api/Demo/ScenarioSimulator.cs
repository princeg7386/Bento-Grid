using CycleGuard.Api.Configuration;
using CycleGuard.Api.Data;
using CycleGuard.Api.Domain;
using CycleGuard.Api.Downstream;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CycleGuard.Api.Demo;

public sealed record ScenarioResult(
    int JobsCreated,
    DateTime CycleCloseUtc,
    double TimeScale,
    int PermanentFailures,
    int OutageStalledJobs,
    int IntermittentGatewayJobs,
    int DuplicateSubmissions,
    long OrphanedPaymentJobId,
    DateTime OutageRecoversAtUtc);

public sealed record KillWorkerResult(bool Killed, long? JobId, string? Worker, string? Reason);

/// <summary>
/// Builds "last night" from a fixed seed, so the same story appears every single run: an
/// endpoint outage that heals, a handful of permanent failures carrying fake PHI, an ACH
/// gateway with intermittent 503s, a genuine duplicate submission, and one payment job
/// abandoned by a dead worker.
/// </summary>
public sealed class ScenarioSimulator(
    IDbContextFactory<CycleGuardDbContext> dbContextFactory,
    TimeProvider timeProvider,
    IOptions<CycleGuardOptions> options,
    SimulationState simulation,
    OutageRegistry outages,
    RawErrorVault rawErrors,
    ILogger<ScenarioSimulator> logger)
{
    private CycleGuardOptions Options => options.Value;

    public async Task<ScenarioResult> SeedLastNightAsync(CancellationToken cancellationToken = default)
    {
        await ResetAsync(cancellationToken);

        var demo = Options.Demo;
        simulation.TimeScale = demo.TimeScale;

        var random = new Random(demo.Seed);
        var nowTicks = timeProvider.UtcTicks();

        var cycleCloseTicks = nowTicks + RealTicks(demo.CycleCloseHours * 3600);
        simulation.CycleCloseTicks = cycleCloseTicks;
        simulation.ScenarioSeededTicks = nowTicks;

        // state-b-mmis is down for the first 25 minutes of cycle time, then recovers on its own.
        var outageRecoversAt = nowTicks + RealTicks(25 * 60);
        outages.ScheduleOutage(JobTypeCatalog.StateBMmis, outageRecoversAt);

        var jobs = new List<Job>(demo.JobCount);

        // ---- 20 encounter submissions stuck behind the state-b-mmis outage -------------
        const int outageStalled = 20;
        for (var index = 0; index < outageStalled; index++)
        {
            jobs.Add(BuildJob(
                random,
                JobType.EncounterSubmission,
                JobTypeCatalog.StateBMmis,
                nowTicks,
                deadlineSimSeconds: random.Next(90, 240) * 60,
                amountCents: random.Next(8_000, 90_000),
                script: null,
                keySuffix: $"outage-{index:D3}"));
        }

        // ---- 8 permanent failures, each carrying synthetic PHI in its message ----------
        string[] permanentSignatures =
        [
            FailureSignatures.ValidationMissingMemberId,
            FailureSignatures.ValidationUnknownProviderId,
            FailureSignatures.ValidationInvalidProcedureCode,
            FailureSignatures.MemberNotEnrolled,
            FailureSignatures.AchAccountClosed,
            FailureSignatures.ValidationNegativeAmount,
            FailureSignatures.DuplicateClaimId,
            FailureSignatures.AchDailyLimitExceeded
        ];

        foreach (var signature in permanentSignatures)
        {
            var type = signature is FailureSignatures.AchAccountClosed
                or FailureSignatures.AchDailyLimitExceeded
                or FailureSignatures.ValidationNegativeAmount
                ? JobType.PaymentRunDisbursement
                : signature is FailureSignatures.ValidationInvalidProcedureCode or FailureSignatures.DuplicateClaimId
                    ? JobType.ClaimsBatchAdjudication
                    : JobType.EncounterSubmission;

            jobs.Add(BuildJob(
                random,
                type,
                type == JobType.EncounterSubmission ? JobTypeCatalog.StateAMmis : JobTypeCatalog.DefaultEndpointFor(type),
                nowTicks,
                deadlineSimSeconds: random.Next(45, 200) * 60,
                amountCents: type == JobType.PaymentRunDisbursement ? random.Next(900_000, 9_500_000) : random.Next(20_000, 400_000),
                script: new FailureScript { Signature = signature, FailUntilAttempt = 0 },
                keySuffix: $"permanent-{signature}"));
        }

        // ---- intermittent ach-gateway 503s that heal through backoff -------------------
        const int intermittentGateway = 12;
        for (var index = 0; index < intermittentGateway; index++)
        {
            jobs.Add(BuildJob(
                random,
                JobType.PaymentRunDisbursement,
                JobTypeCatalog.AchGateway,
                nowTicks,
                deadlineSimSeconds: demo.CycleCloseHours * 3600,
                amountCents: random.Next(1_200_000, 9_500_000),
                script: new FailureScript { Signature = FailureSignatures.Http503, FailUntilAttempt = index % 2 == 0 ? 1 : 2 },
                keySuffix: $"ach-503-{index:D2}"));
        }

        // ---- 2 submissions pointed at one downstream key: a real duplicate ------------
        var sharedDownstreamKey = "ACH-DISBURSEMENT-2026-03-14-PROVIDER-88117";
        for (var index = 0; index < 2; index++)
        {
            var duplicate = BuildJob(
                random,
                JobType.PaymentRunDisbursement,
                JobTypeCatalog.AchGateway,
                nowTicks,
                deadlineSimSeconds: demo.CycleCloseHours * 3600,
                amountCents: 4_412_900,
                script: null,
                keySuffix: $"duplicate-{index:D2}");

            // Distinct jobs, one downstream effect. The ledger is what refuses the second.
            duplicate.DownstreamIdempotencyKey = sharedDownstreamKey;
            jobs.Add(duplicate);
        }

        // ---- the rest of the night: mostly clean, some transient timeouts -------------
        var remaining = Math.Max(0, demo.JobCount - jobs.Count - 1);
        for (var index = 0; index < remaining; index++)
        {
            var roll = random.NextDouble();
            var type = roll switch
            {
                < 0.55 => JobType.ClaimsBatchAdjudication,
                < 0.88 => JobType.EncounterSubmission,
                _ => JobType.PaymentRunDisbursement
            };

            var endpoint = type == JobType.EncounterSubmission
                ? (random.NextDouble() < 0.75 ? JobTypeCatalog.StateAMmis : JobTypeCatalog.StateBMmis)
                : JobTypeCatalog.DefaultEndpointFor(type);

            // A slice of the night is already past its deadline, and a slice is inside the
            // at-risk window, so the dashboard has something to sort on the moment it loads.
            var deadlineSimSeconds = index switch
            {
                < 6 => -random.Next(5, 40) * 60,
                < 30 => random.Next(5, 40) * 60,
                _ => random.Next(60, (int)(demo.CycleCloseHours * 60) + 240) * 60
            };

            FailureScript? script = random.NextDouble() < 0.12
                ? new FailureScript { Signature = FailureSignatures.DownstreamTimeout, FailUntilAttempt = 1 }
                : null;

            var amount = type switch
            {
                JobType.PaymentRunDisbursement => random.Next(800_000, 9_500_000),
                JobType.ClaimsBatchAdjudication => random.Next(50_000, 900_000),
                _ => random.Next(5_000, 80_000)
            };

            jobs.Add(BuildJob(random, type, endpoint, nowTicks, deadlineSimSeconds, amount, script, $"night-{index:D4}"));
        }

        // ---- one payment job abandoned by a worker that died mid-run ------------------
        var orphan = BuildJob(
            random,
            JobType.PaymentRunDisbursement,
            JobTypeCatalog.AchGateway,
            nowTicks,
            deadlineSimSeconds: demo.CycleCloseHours * 3600,
            amountCents: 7_318_400,
            script: null,
            keySuffix: "orphaned-payment");

        // Left mid-flight: Running, with a lease that already lapsed. The reaper will find it.
        orphan.State = JobState.Running;
        orphan.Attempts = 1;
        orphan.ClaimedBy = "worker-3";
        orphan.LeaseExpiresTicks = nowTicks - TimeSpan.TicksPerSecond;
        orphan.NextAttemptTicks = null;
        jobs.Add(orphan);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.Jobs.AddRange(jobs);
        await dbContext.SaveChangesAsync(cancellationToken);

        // The orphan needs an open attempt row, otherwise there is nothing for the reaper to
        // close out and the attempt timeline would lie about what happened.
        dbContext.JobAttempts.Add(new JobAttempt
        {
            JobId = orphan.Id,
            AttemptNumber = 1,
            WorkerId = "worker-3",
            StartedTicks = nowTicks - (5 * TimeSpan.TicksPerSecond)
        });

        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = orphan.Id,
            AtTicks = nowTicks,
            Actor = "scenario",
            EventType = AuditEventTypes.WorkerKilled,
            Details = "worker-3 was killed mid-disbursement. Its lease has already lapsed."
        });

        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = null,
            AtTicks = nowTicks,
            Actor = "scenario",
            EventType = AuditEventTypes.ScenarioSeeded,
            Details = $"Seeded {jobs.Count} synthetic jobs with seed {demo.Seed} at time scale {demo.TimeScale}."
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded last-night scenario: {Count} jobs, cycle closes at {Close:O}, state-b-mmis recovers at {Recover:O}.",
            jobs.Count,
            Clock.ToUtc(cycleCloseTicks),
            Clock.ToUtc(outageRecoversAt));

        return new ScenarioResult(
            jobs.Count,
            Clock.ToUtc(cycleCloseTicks),
            simulation.TimeScale,
            permanentSignatures.Length,
            outageStalled,
            intermittentGateway,
            2,
            orphan.Id,
            Clock.ToUtc(outageRecoversAt));
    }

    /// <summary>
    /// Expire the lease on a payment job that is running right now, as if the worker holding
    /// it had been killed. The reaper hands it to another worker; the ledger stops it paying
    /// twice even if the original worker had already committed.
    /// </summary>
    public async Task<KillWorkerResult> KillWorkerMidPaymentAsync(CancellationToken cancellationToken = default)
    {
        var nowTicks = timeProvider.UtcTicks();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var victim = await dbContext.Jobs
            .Where(j => j.State == JobState.Running && j.Type == JobType.PaymentRunDisbursement)
            .OrderBy(j => j.DeadlineTicks)
            .FirstOrDefaultAsync(cancellationToken);

        victim ??= await dbContext.Jobs
            .Where(j => j.State == JobState.Running)
            .OrderBy(j => j.DeadlineTicks)
            .FirstOrDefaultAsync(cancellationToken);

        if (victim is null)
        {
            return new KillWorkerResult(false, null, null, "No job is currently running. Try again while the queue is busy.");
        }

        var worker = victim.ClaimedBy;
        victim.LeaseExpiresTicks = nowTicks - TimeSpan.TicksPerSecond;
        victim.UpdatedTicks = nowTicks;

        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = victim.Id,
            AtTicks = nowTicks,
            Actor = "scenario",
            EventType = AuditEventTypes.WorkerKilled,
            Details = $"Simulated kill of '{worker ?? "unknown"}' mid-job. Lease expired immediately."
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return new KillWorkerResult(true, victim.Id, worker, null);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Child tables first: AuditEvents and the ledger have no cascade from Jobs.
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM JobAttempts;", cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM AuditEvents;", cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM DownstreamLedger;", cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM Jobs;", cancellationToken);

        // Restart identity columns so a reseeded demo has the same job ids as the last run.
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "DELETE FROM sqlite_sequence WHERE name IN ('Jobs', 'JobAttempts', 'AuditEvents', 'DownstreamLedger');",
                cancellationToken);
        }
        catch (Exception exception)
        {
            // sqlite_sequence only exists once an AUTOINCREMENT table has been written to.
            logger.LogDebug(exception, "Could not reset sqlite_sequence; continuing.");
        }

        outages.Clear();
        rawErrors.Clear();
        simulation.Reset();

        dbContext.AuditEvents.Add(new AuditEvent
        {
            JobId = null,
            AtTicks = timeProvider.UtcTicks(),
            Actor = "scenario",
            EventType = AuditEventTypes.ScenarioReset,
            Details = "All synthetic jobs, attempts, ledger entries and events cleared."
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- helpers

    private long RealTicks(double simulatedSeconds)
        => (long)(simulation.ToRealSeconds(simulatedSeconds) * TimeSpan.TicksPerSecond);

    private Job BuildJob(
        Random random,
        JobType type,
        string endpoint,
        long nowTicks,
        double deadlineSimSeconds,
        long amountCents,
        FailureScript? script,
        string keySuffix)
    {
        var first = SyntheticData.FirstNames[random.Next(SyntheticData.FirstNames.Length)];
        var last = SyntheticData.LastNames[random.Next(SyntheticData.LastNames.Length)];

        var payload = new JobPayload
        {
            BatchId = SyntheticData.BatchId(type switch
            {
                JobType.ClaimsBatchAdjudication => "CLM",
                JobType.EncounterSubmission => "ENC",
                _ => "PAY"
            }, random.Next(1, 99_999)),
            MemberCount = random.Next(1, 1200),
            StateCode = SyntheticData.StateCodes[random.Next(SyntheticData.StateCodes.Length)],
            Member = new SyntheticMember
            {
                Id = SyntheticData.MemberId(random),
                Name = $"{first} {last}",
                Dob = SyntheticData.Dob(random),
                Phone = SyntheticData.Phone(random),
                Email = SyntheticData.Email(first, last, random),
                Ssn = SyntheticData.SsnShaped(random),
                ProviderId = SyntheticData.ProviderId(random)
            },
            Script = script
        };

        var key = $"{type}-{keySuffix}";

        return new Job
        {
            Type = type,
            Payload = JobPayloadCodec.Serialise(payload),
            State = JobState.Queued,
            Attempts = 0,
            MaxAttempts = Options.Retry.MaxAttemptsFor(type),
            DeadlineTicks = nowTicks + RealTicks(deadlineSimSeconds),
            AmountAtStakeCents = amountCents,
            IdempotencyKey = key,
            DownstreamIdempotencyKey = key,
            DownstreamEndpoint = endpoint,
            NextAttemptTicks = nowTicks,
            CreatedTicks = nowTicks,
            UpdatedTicks = nowTicks,
            IsSynthetic = true
        };
    }
}
