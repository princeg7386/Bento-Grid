using CycleGuard.Api.Domain;

namespace CycleGuard.Tests;

public class LeaseRecoveryTests
{
    [Fact]
    public async Task ALiveLeaseIsNotReclaimed()
    {
        await using var harness = await QueueHarness.CreateAsync();

        await harness.EnqueueAsync();
        await harness.Queue.TryClaimAsync("worker-1");

        harness.Advance(TimeSpan.FromSeconds(29));

        Assert.Empty(await harness.Queue.ReclaimExpiredLeasesAsync());
    }

    [Fact]
    public async Task AnExpiredLeaseReturnsTheJobToTheQueueForAnotherWorker()
    {
        await using var harness = await QueueHarness.CreateAsync();

        var job = await harness.EnqueueAsync(JobType.PaymentRunDisbursement, amountCents: 7_318_400);

        var claimed = await harness.Queue.TryClaimAsync("worker-3");
        Assert.NotNull(claimed);
        await harness.Queue.RecordAttemptStartAsync(claimed!, "worker-3");

        // worker-3 dies here: no completion, no retry, nothing.
        harness.Advance(TimeSpan.FromSeconds(31));

        var reclaimed = await harness.Queue.ReclaimExpiredLeasesAsync();

        var single = Assert.Single(reclaimed);
        Assert.Equal(job.Id, single.JobId);
        Assert.Equal("worker-3", single.PreviousWorker);

        var reloaded = await harness.ReloadAsync(job.Id);
        Assert.Equal(JobState.Queued, reloaded.State);
        Assert.Null(reloaded.LeaseExpiresTicks);

        // The interrupted attempt is closed out honestly rather than left dangling.
        var attempts = await harness.AttemptsAsync(job.Id);
        var first = Assert.Single(attempts);
        Assert.Equal(AttemptOutcome.LeaseExpired, first.Outcome);
        Assert.Equal(FailureSignatures.LeaseExpired, first.FailureSignature);
        Assert.NotNull(first.FinishedTicks);

        // Another worker picks it up, and the history is still there.
        var second = await harness.Queue.TryClaimAsync("worker-5");
        Assert.NotNull(second);
        Assert.Equal(job.Id, second!.Id);
        Assert.Equal(2, second.Attempts);

        var events = await harness.EventsAsync(job.Id);
        Assert.Contains(events, e => e.EventType == AuditEventTypes.LeaseExpired && e.Actor == "lease-reaper");
    }

    [Fact]
    public async Task TheAbandonedWorkerCanNoLongerCompleteTheJob()
    {
        await using var harness = await QueueHarness.CreateAsync();

        var job = await harness.EnqueueAsync();
        var claimed = await harness.Queue.TryClaimAsync("worker-1");
        var attempt = await harness.Queue.RecordAttemptStartAsync(claimed!, "worker-1");

        harness.Advance(TimeSpan.FromSeconds(31));
        await harness.Queue.ReclaimExpiredLeasesAsync();

        // worker-1 comes back from the dead and tries to commit. It must be refused.
        var completion = await harness.Queue.MarkSucceededAsync(claimed!, attempt.Id, "worker-1");

        Assert.False(completion.Committed);
        Assert.Equal(JobState.Queued, (await harness.ReloadAsync(job.Id)).State);
    }

    [Fact]
    public async Task ARecoveredPaymentJobStillOnlyDisbursesOnce()
    {
        await using var harness = await QueueHarness.CreateAsync();

        var job = await harness.EnqueueAsync(
            JobType.PaymentRunDisbursement,
            amountCents: 5_000_000,
            downstreamKey: "ACH-RECOVERED-ONCE");

        // First worker claims, commits the disbursement, and then its lease is reaped anyway.
        var first = await harness.Queue.TryClaimAsync("worker-1");
        var firstAttempt = await harness.Queue.RecordAttemptStartAsync(first!, "worker-1");
        await harness.Queue.MarkSucceededAsync(first!, firstAttempt.Id, "worker-1");

        Assert.Equal(1, await harness.LedgerCountAsync("ACH-RECOVERED-ONCE"));
        Assert.Equal(JobState.Succeeded, (await harness.ReloadAsync(job.Id)).State);

        // A succeeded job is not claimable, so there is no second disbursement to make.
        Assert.Null(await harness.Queue.TryClaimAsync("worker-2"));
        Assert.Equal(1, await harness.LedgerCountAsync("ACH-RECOVERED-ONCE"));
    }

    [Fact]
    public async Task TheSeededScenarioLeavesOnePaymentJobForTheReaperToRecover()
    {
        await using var harness = await QueueHarness.CreateAsync(options => options.Demo.JobCount = 60);

        var scenario = await harness.Scenario.SeedLastNightAsync();

        var orphan = await harness.ReloadAsync(scenario.OrphanedPaymentJobId);
        Assert.Equal(JobState.Running, orphan.State);
        Assert.Equal("worker-3", orphan.ClaimedBy);
        Assert.True(orphan.LeaseExpiresTicks < harness.Time.UtcTicks());

        var reclaimed = await harness.Queue.ReclaimExpiredLeasesAsync();
        Assert.Contains(reclaimed, r => r.JobId == scenario.OrphanedPaymentJobId);

        Assert.Equal(JobState.Queued, (await harness.ReloadAsync(scenario.OrphanedPaymentJobId)).State);
    }

    [Fact]
    public async Task KillingAWorkerMidJobExpiresItsLeaseImmediately()
    {
        await using var harness = await QueueHarness.CreateAsync();

        await harness.EnqueueAsync(JobType.PaymentRunDisbursement);
        var claimed = await harness.Queue.TryClaimAsync("worker-2");
        await harness.Queue.RecordAttemptStartAsync(claimed!, "worker-2");

        var result = await harness.Scenario.KillWorkerMidPaymentAsync();

        Assert.True(result.Killed);
        Assert.Equal(claimed!.Id, result.JobId);
        Assert.Equal("worker-2", result.Worker);

        // No time needs to pass: the lease is already in the past.
        Assert.Single(await harness.Queue.ReclaimExpiredLeasesAsync());
    }

    [Fact]
    public async Task KillingAWorkerWithNothingRunningIsRefusedCleanly()
    {
        await using var harness = await QueueHarness.CreateAsync();

        var result = await harness.Scenario.KillWorkerMidPaymentAsync();

        Assert.False(result.Killed);
        Assert.NotNull(result.Reason);
    }
}
