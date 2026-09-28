using CycleGuard.Api.Configuration;
using CycleGuard.Api.Domain;
using CycleGuard.Api.Downstream;

namespace CycleGuard.Tests;

public class RetryRoutingTests
{
    [Fact]
    public async Task TransientFailureSchedulesARetryOnTheBackoffCurve()
    {
        await using var harness = await QueueHarness.CreateAsync();

        var job = await harness.EnqueueAsync(
            script: new FailureScript { Signature = FailureSignatures.Http503, FailUntilAttempt = 99 });

        await harness.RunOneAsync();

        var reloaded = await harness.ReloadAsync(job.Id);

        Assert.Equal(JobState.RetryScheduled, reloaded.State);
        Assert.Equal(1, reloaded.Attempts);
        Assert.Equal(FailureClass.Transient, reloaded.LastFailureClass);
        Assert.Equal(FailureSignatures.Http503, reloaded.LastFailureSignature);

        // Base 30s, jitter off, time scale 1: the next attempt is exactly 30s out.
        Assert.Equal(Clock.Ticks(harness.UtcNow.AddSeconds(30)), reloaded.NextAttemptTicks);
    }

    [Fact]
    public async Task AScheduledRetryIsNotClaimableUntilItsTimeArrives()
    {
        await using var harness = await QueueHarness.CreateAsync();

        await harness.EnqueueAsync(script: new FailureScript { Signature = FailureSignatures.Http503, FailUntilAttempt = 99 });
        await harness.RunOneAsync();

        Assert.Null(await harness.Queue.TryClaimAsync("worker-2"));

        harness.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(await harness.Queue.TryClaimAsync("worker-2"));

        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await harness.Queue.TryClaimAsync("worker-2"));
    }

    [Fact]
    public async Task ExhaustedRetriesEndInTheDeadLetterTable()
    {
        // MaxRetries 3 means four attempts in total.
        await using var harness = await QueueHarness.CreateAsync();

        var job = await harness.EnqueueAsync(
            script: new FailureScript { Signature = FailureSignatures.DownstreamTimeout, FailUntilAttempt = 99 });

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.NotNull(await harness.RunOneAsync());
            harness.Advance(TimeSpan.FromMinutes(20));
        }

        var reloaded = await harness.ReloadAsync(job.Id);

        Assert.Equal(JobState.DeadLettered, reloaded.State);
        Assert.Equal(4, reloaded.Attempts);
        Assert.False(reloaded.DeadLetterResolved);

        // And it stays there: nothing is claimable any more.
        Assert.Null(await harness.RunOneAsync());

        var events = await harness.EventsAsync(job.Id);
        Assert.Equal(3, events.Count(e => e.EventType == AuditEventTypes.RetryScheduled));
        Assert.Single(events, e => e.EventType == AuditEventTypes.DeadLettered);
    }

    [Fact]
    public async Task PermanentFailureGoesStraightToDeadLetterWithNoRetries()
    {
        await using var harness = await QueueHarness.CreateAsync();

        var job = await harness.EnqueueAsync(
            script: new FailureScript { Signature = FailureSignatures.ValidationUnknownProviderId, FailUntilAttempt = 0 });

        await harness.RunOneAsync();

        var reloaded = await harness.ReloadAsync(job.Id);

        Assert.Equal(JobState.DeadLettered, reloaded.State);
        Assert.Equal(1, reloaded.Attempts);
        Assert.Equal(FailureClass.Permanent, reloaded.LastFailureClass);
        Assert.Null(reloaded.NextAttemptTicks);

        var events = await harness.EventsAsync(job.Id);
        Assert.DoesNotContain(events, e => e.EventType == AuditEventTypes.RetryScheduled);
    }

    [Fact]
    public async Task FailTwiceThenSucceedProducesThreeAttemptsInOrder()
    {
        await using var harness = await QueueHarness.CreateAsync();

        var job = await harness.EnqueueAsync(
            script: new FailureScript { Signature = FailureSignatures.Http503, FailUntilAttempt = 2 });

        for (var round = 0; round < 3; round++)
        {
            await harness.RunOneAsync();
            harness.Advance(TimeSpan.FromMinutes(5));
        }

        var reloaded = await harness.ReloadAsync(job.Id);
        Assert.Equal(JobState.Succeeded, reloaded.State);
        Assert.Equal(3, reloaded.Attempts);

        var attempts = await harness.AttemptsAsync(job.Id);
        Assert.Equal(3, attempts.Count);
        Assert.Equal(
            [AttemptOutcome.TransientFailure, AttemptOutcome.TransientFailure, AttemptOutcome.Succeeded],
            attempts.Select(a => a.Outcome).ToArray());

        // The two failures recorded the backoff they were granted; the success did not.
        Assert.Equal([30, 60], attempts.Take(2).Select(a => a.BackoffSeconds).ToArray());
        Assert.Null(attempts[2].BackoffSeconds);
    }

    [Fact]
    public async Task PerJobTypeRetryOverridesAreApplied()
    {
        await using var harness = await QueueHarness.CreateAsync(options =>
        {
            options.Retry.MaxRetries = 2;
            options.Retry.PerJobType["PaymentRunDisbursement"] = new RetryOverride
            {
                MaxRetries = 6,
                BaseSeconds = 10
            };
        });

        var payment = await harness.EnqueueAsync(JobType.PaymentRunDisbursement);
        var claim = await harness.EnqueueAsync(JobType.ClaimsBatchAdjudication);

        Assert.Equal(7, payment.MaxAttempts);
        Assert.Equal(3, claim.MaxAttempts);
    }

    [Fact]
    public async Task PersistedErrorTextIsMaskedWhileTheRawTextStaysInMemoryOnly()
    {
        await using var harness = await QueueHarness.CreateAsync();

        var job = await harness.EnqueueAsync(
            script: new FailureScript { Signature = FailureSignatures.ValidationMissingMemberId, FailUntilAttempt = 0 });

        await harness.RunOneAsync();

        var reloaded = await harness.ReloadAsync(job.Id);
        Assert.NotNull(reloaded.LastErrorMasked);
        Assert.DoesNotContain("Dana Whitfield", reloaded.LastErrorMasked);
        Assert.DoesNotContain("912-55-1173", reloaded.LastErrorMasked);
        Assert.DoesNotContain("MBR-4471902", reloaded.LastStackTraceMasked ?? string.Empty);
        Assert.Contains(PhiMasker.NameToken, reloaded.LastErrorMasked);

        // The raw text exists, but only in process memory.
        var attempts = await harness.AttemptsAsync(job.Id);
        var raw = harness.RawErrors.Get(attempts[0].Id);
        Assert.NotNull(raw);
        Assert.Contains("Dana Whitfield", raw!.Message);
    }

    [Fact]
    public async Task ACleanJobSucceedsOnItsFirstAttempt()
    {
        await using var harness = await QueueHarness.CreateAsync();

        var job = await harness.EnqueueAsync();
        await harness.RunOneAsync();

        var reloaded = await harness.ReloadAsync(job.Id);
        Assert.Equal(JobState.Succeeded, reloaded.State);
        Assert.Equal(1, reloaded.Attempts);
        Assert.NotNull(reloaded.CompletedTicks);
    }
}
