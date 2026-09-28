using CycleGuard.Api.Domain;

namespace CycleGuard.Tests;

public class BackoffTests
{
    private static readonly BackoffPolicy Policy = new(BaseSeconds: 30, CapSeconds: 900, MaxRetries: 8);

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 900)]   // 960 would exceed the cap
    [InlineData(7, 900)]
    [InlineData(20, 900)]  // far past the cap, and must not overflow
    public void DelayDoublesUntilItHitsTheCap(int attempt, int expected)
        => Assert.Equal(expected, BackoffCalculator.DelaySeconds(Policy, attempt));

    [Fact]
    public void AttemptNumbersAreOneBased()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BackoffCalculator.DelaySeconds(Policy, 0));

    [Fact]
    public void DelayNeverDropsBelowOneSecond()
    {
        var tiny = new BackoffPolicy(BaseSeconds: 0, CapSeconds: 5, MaxRetries: 3);
        Assert.Equal(1, BackoffCalculator.DelaySeconds(tiny, 1));
    }

    [Fact]
    public void JitterStaysWithinTheConfiguredFraction()
    {
        var policy = new BackoffPolicy(30, 900, 8, JitterEnabled: true, JitterFraction: 0.2);

        for (var jobId = 1; jobId <= 200; jobId++)
        {
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var plain = BackoffCalculator.DelaySeconds(policy, attempt);
                var jittered = BackoffCalculator.DelaySecondsWithJitter(policy, attempt, jobId);

                Assert.InRange(jittered, (int)Math.Floor(plain * 0.8) - 1, (int)Math.Ceiling(plain * 1.2) + 1);
            }
        }
    }

    [Fact]
    public void JitterIsDeterministicForTheSameJobAndAttempt()
    {
        var policy = new BackoffPolicy(30, 900, 8, JitterEnabled: true, JitterFraction: 0.2);

        var first = BackoffCalculator.DelaySecondsWithJitter(policy, 3, jobId: 4242);
        var second = BackoffCalculator.DelaySecondsWithJitter(policy, 3, jobId: 4242);

        Assert.Equal(first, second);
    }

    [Fact]
    public void JitterDisabledReturnsTheExactCurve()
    {
        var policy = new BackoffPolicy(30, 900, 8, JitterEnabled: false, JitterFraction: 0.5);
        Assert.Equal(120, BackoffCalculator.DelaySecondsWithJitter(policy, 3, jobId: 99));
    }

    [Fact]
    public void RemainingBackoffSumsTheAttemptsStillAhead()
    {
        // Four attempts allowed, one already made: waits of 60 + 120 + 240 remain.
        var remaining = BackoffCalculator.RemainingBackoffSeconds(Policy, attemptsSoFar: 1, maxAttempts: 4);
        Assert.Equal(30 + 60 + 120, remaining);
    }

    [Fact]
    public void RemainingBackoffIsZeroOnTheLastAttempt()
        => Assert.Equal(0, BackoffCalculator.RemainingBackoffSeconds(Policy, attemptsSoFar: 4, maxAttempts: 4));

    [Fact]
    public void UpcomingScheduleNumbersTheNextAttempts()
    {
        var schedule = BackoffCalculator.UpcomingSchedule(Policy, attemptsSoFar: 1, maxAttempts: 4, jobId: 7);

        Assert.Equal(3, schedule.Count);
        Assert.Equal([2, 3, 4], schedule.Select(slot => slot.Attempt).ToArray());
        Assert.Equal([30, 60, 120], schedule.Select(slot => slot.DelaySeconds).ToArray());
    }
}
