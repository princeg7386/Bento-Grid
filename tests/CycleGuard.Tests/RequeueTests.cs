using CycleGuard.Api.Domain;
using CycleGuard.Api.Downstream;
using CycleGuard.Api.Queue;

namespace CycleGuard.Tests;

public class RequeueTests
{
    /// <summary>
    /// A payment job that dead-letters after one attempt and would succeed on the next, which
    /// is the shape a real "fix it and retry" looks like.
    /// </summary>
    private static async Task<Job> DeadLetteredPaymentAsync(QueueHarness harness, string downstreamKey)
    {
        var job = await harness.EnqueueAsync(
            JobType.PaymentRunDisbursement,
            amountCents: 4_412_900,
            script: new FailureScript { Signature = FailureSignatures.Http503, FailUntilAttempt = 1 },
            downstreamKey: downstreamKey,
            maxAttempts: 1);

        await harness.RunOneAsync();

        var reloaded = await harness.ReloadAsync(job.Id);
        Assert.Equal(JobState.DeadLettered, reloaded.State);
        return reloaded;
    }

    [Fact]
    public async Task RequeueDemandsAnAnalystNameAndANote()
    {
        await using var harness = await QueueHarness.CreateAsync();
        var job = await DeadLetteredPaymentAsync(harness, "ACH-NEEDS-DETAILS");

        Assert.Equal(RequeueOutcome.MissingAnalystDetails, (await harness.Queue.RequeueAsync(job.Id, "", "note")).Outcome);
        Assert.Equal(RequeueOutcome.MissingAnalystDetails, (await harness.Queue.RequeueAsync(job.Id, "Priya", "  ")).Outcome);

        // Still dead-lettered: a rejected requeue changes nothing.
        Assert.Equal(JobState.DeadLettered, (await harness.ReloadAsync(job.Id)).State);
    }

    [Fact]
    public async Task RequeueKeepsTheHistoryAndTheIdempotencyKey()
    {
        await using var harness = await QueueHarness.CreateAsync();
        var job = await DeadLetteredPaymentAsync(harness, "ACH-KEEPS-KEY");

        var result = await harness.Queue.RequeueAsync(job.Id, "Priya S", "Bank details corrected with the provider.");

        Assert.True(result.Success);

        var reloaded = await harness.ReloadAsync(job.Id);
        Assert.Equal(JobState.Queued, reloaded.State);
        Assert.Equal(1, reloaded.RequeueCount);
        Assert.True(reloaded.DeadLetterResolved);

        // The key never changes, which is precisely what stops a second disbursement.
        Assert.Equal(job.IdempotencyKey, reloaded.IdempotencyKey);
        Assert.Equal("ACH-KEEPS-KEY", reloaded.DownstreamIdempotencyKey);

        // Attempt one is still on the record.
        Assert.Single(await harness.AttemptsAsync(job.Id));
        Assert.Equal(1, reloaded.Attempts);

        var events = await harness.EventsAsync(job.Id);
        var requeue = Assert.Single(events, e => e.EventType == AuditEventTypes.Requeued);
        Assert.Equal("Priya S", requeue.Actor);
        Assert.Contains("Bank details corrected", requeue.Details);
    }

    [Fact]
    public async Task ARequeuedJobGetsExactlyOneMoreAttempt()
    {
        await using var harness = await QueueHarness.CreateAsync();
        var job = await DeadLetteredPaymentAsync(harness, "ACH-ONE-MORE");

        await harness.Queue.RequeueAsync(job.Id, "Priya S", "Retrying once.");

        var reloaded = await harness.ReloadAsync(job.Id);
        Assert.Equal(reloaded.Attempts + 1, reloaded.MaxAttempts);

        Assert.NotNull(await harness.RunOneAsync());
        Assert.Equal(JobState.Succeeded, (await harness.ReloadAsync(job.Id)).State);
    }

    [Fact]
    public async Task AJobThatIsNotDeadLetteredCannotBeRequeued()
    {
        await using var harness = await QueueHarness.CreateAsync();
        var job = await harness.EnqueueAsync();

        var result = await harness.Queue.RequeueAsync(job.Id, "Priya S", "Trying anyway.");

        Assert.Equal(RequeueOutcome.NotDeadLettered, result.Outcome);
        Assert.Equal(JobState.Queued, (await harness.ReloadAsync(job.Id)).State);
    }

    [Fact]
    public async Task RequeueingAMissingJobIsNotFound()
    {
        await using var harness = await QueueHarness.CreateAsync();
        Assert.Equal(RequeueOutcome.NotFound, (await harness.Queue.RequeueAsync(987654, "Priya S", "n/a")).Outcome);
    }

    [Fact]
    public async Task ThreeConcurrentRequeuesProduceExactlyOneWinnerAndOneDisbursement()
    {
        await using var harness = await QueueHarness.CreateAsync(useSystemClock: true);

        const string downstreamKey = "ACH-TRIPLE-REQUEUE";
        var job = await DeadLetteredPaymentAsync(harness, downstreamKey);

        // Three analysts hit the button at the same moment.
        var attempts = await Task.WhenAll(
            Task.Run(() => harness.Queue.RequeueAsync(job.Id, "Priya S", "First.")),
            Task.Run(() => harness.Queue.RequeueAsync(job.Id, "Marcus O", "Second.")),
            Task.Run(() => harness.Queue.RequeueAsync(job.Id, "Elena C", "Third.")));

        Assert.Equal(1, attempts.Count(a => a.Outcome == RequeueOutcome.Requeued));
        Assert.Equal(2, attempts.Count(a => a.Outcome == RequeueOutcome.NotDeadLettered));

        // The single winner runs, and the money moves exactly once.
        Assert.NotNull(await harness.RunOneAsync());
        Assert.Equal(JobState.Succeeded, (await harness.ReloadAsync(job.Id)).State);
        Assert.Equal(1, await harness.LedgerCountAsync(downstreamKey));

        var reloaded = await harness.ReloadAsync(job.Id);
        Assert.Equal(1, reloaded.RequeueCount);

        // Nothing left to claim, so there is no second run to pay twice.
        Assert.Null(await harness.RunOneAsync());
    }

    [Fact]
    public async Task TwoJobsSharingADownstreamKeyProduceOneDisbursementAndOnePreventedDuplicate()
    {
        await using var harness = await QueueHarness.CreateAsync();

        const string sharedKey = "ACH-DISBURSEMENT-SHARED";

        var first = await harness.EnqueueAsync(
            JobType.PaymentRunDisbursement, amountCents: 4_412_900,
            idempotencyKey: "pay-a", downstreamKey: sharedKey);

        var second = await harness.EnqueueAsync(
            JobType.PaymentRunDisbursement, amountCents: 4_412_900,
            idempotencyKey: "pay-b", downstreamKey: sharedKey);

        await harness.RunOneAsync("worker-1");
        await harness.RunOneAsync("worker-2");

        // Both jobs completed, because neither of them failed.
        Assert.Equal(JobState.Succeeded, (await harness.ReloadAsync(first.Id)).State);
        Assert.Equal(JobState.Succeeded, (await harness.ReloadAsync(second.Id)).State);

        // But the downstream only ever recorded one effect.
        Assert.Equal(1, await harness.LedgerCountAsync(sharedKey));

        var prevented = (await harness.EventsAsync())
            .Where(e => e.EventType == AuditEventTypes.DuplicatePrevented)
            .ToList();

        Assert.Single(prevented);
    }

    [Fact]
    public async Task ADuplicateIdempotencyKeyIsRefusedAtEnqueueTime()
    {
        await using var harness = await QueueHarness.CreateAsync();

        await harness.EnqueueAsync(idempotencyKey: "only-once");

        var second = await harness.Queue.EnqueueAsync(new EnqueueRequest(
            JobType.ClaimsBatchAdjudication,
            "{}",
            harness.UtcNow.AddHours(2),
            1000,
            IdempotencyKey: "only-once"));

        Assert.True(second.DuplicateKey);
        Assert.Null(second.Job);
    }
}
