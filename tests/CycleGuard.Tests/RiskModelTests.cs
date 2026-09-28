using CycleGuard.Api.Domain;

namespace CycleGuard.Tests;

/// <summary>
/// One test per branch of the risk model. The worked examples in docs/risk-model.md are the
/// same two scenarios as <see cref="WorkedExampleOne_BackoffCannotFitBeforeDeadline"/> and
/// <see cref="WorkedExampleTwo_PlentyOfSlackIsOnTrack"/>.
/// </summary>
public class RiskModelTests
{
    private static readonly DateTime Now = new(2026, 3, 14, 2, 0, 0, DateTimeKind.Utc);

    private static readonly BackoffPolicy Policy = new(BaseSeconds: 30, CapSeconds: 900, MaxRetries: 3);

    private static RiskContext Context(double thresholdMinutes = 45, double timeScale = 1)
        => new(Now, thresholdMinutes, timeScale, Policy);

    private static Job Job(
        JobState state = JobState.Queued,
        TimeSpan? deadlineIn = null,
        int attempts = 0,
        int maxAttempts = 4,
        TimeSpan? nextAttemptIn = null,
        bool deadLetterResolved = false)
        => new()
        {
            Id = 1,
            Type = JobType.PaymentRunDisbursement,
            State = state,
            Attempts = attempts,
            MaxAttempts = maxAttempts,
            DeadlineTicks = Clock.Ticks(Now.Add(deadlineIn ?? TimeSpan.FromHours(4))),
            NextAttemptTicks = nextAttemptIn is null ? null : Clock.Ticks(Now.Add(nextAttemptIn.Value)),
            DeadLetterResolved = deadLetterResolved,
            AmountAtStakeCents = 1_000_000
        };

    [Fact]
    public void SucceededIsDone()
    {
        // Past its deadline, but it landed. Nobody needs to look at it.
        var assessment = RiskEvaluator.Evaluate(
            Job(JobState.Succeeded, deadlineIn: TimeSpan.FromHours(-2)), Context());

        Assert.Equal(RiskLevel.Done, assessment.Level);
        Assert.Equal("succeeded", assessment.ReasonCode);
    }

    [Fact]
    public void CancelledIsDoneNotBreached()
    {
        // Documented deviation: an operator already decided this should not run, so a passed
        // deadline is not a breach.
        var assessment = RiskEvaluator.Evaluate(
            Job(JobState.Cancelled, deadlineIn: TimeSpan.FromHours(-2)), Context());

        Assert.Equal(RiskLevel.Done, assessment.Level);
        Assert.Equal("cancelled", assessment.ReasonCode);
    }

    [Fact]
    public void PastDeadlineAndUnfinishedIsBreached()
    {
        var assessment = RiskEvaluator.Evaluate(
            Job(JobState.RetryScheduled, deadlineIn: TimeSpan.FromMinutes(-30)), Context());

        Assert.Equal(RiskLevel.Breached, assessment.Level);
        Assert.Equal("past_deadline", assessment.ReasonCode);
        Assert.Equal(-1800, assessment.SecondsToDeadline);
    }

    [Fact]
    public void BreachedOutranksNeedsHumanWhenBothApply()
    {
        var assessment = RiskEvaluator.Evaluate(
            Job(JobState.DeadLettered, deadlineIn: TimeSpan.FromMinutes(-5)), Context());

        Assert.Equal(RiskLevel.Breached, assessment.Level);
    }

    [Fact]
    public void UnresolvedDeadLetterNeedsAHuman()
    {
        var assessment = RiskEvaluator.Evaluate(
            Job(JobState.DeadLettered, deadlineIn: TimeSpan.FromHours(3)), Context());

        Assert.Equal(RiskLevel.NeedsHuman, assessment.Level);
        Assert.Equal("dead_lettered", assessment.ReasonCode);
    }

    [Fact]
    public void ResolvedDeadLetterNoLongerNeedsAHuman()
    {
        var assessment = RiskEvaluator.Evaluate(
            Job(JobState.DeadLettered, deadlineIn: TimeSpan.FromHours(3), deadLetterResolved: true), Context());

        Assert.NotEqual(RiskLevel.NeedsHuman, assessment.Level);
    }

    [Fact]
    public void ThinSlackIsAtRisk()
    {
        var assessment = RiskEvaluator.Evaluate(
            Job(deadlineIn: TimeSpan.FromMinutes(20)), Context(thresholdMinutes: 45));

        Assert.Equal(RiskLevel.AtRisk, assessment.Level);
        Assert.Equal("deadline_near", assessment.ReasonCode);
    }

    [Fact]
    public void NextAttemptAfterTheDeadlineIsAtRisk()
    {
        // Two hours of slack, so the deadline is not near, but the retry lands too late.
        var assessment = RiskEvaluator.Evaluate(
            Job(JobState.RetryScheduled, deadlineIn: TimeSpan.FromHours(2), attempts: 1, nextAttemptIn: TimeSpan.FromHours(3)),
            Context());

        Assert.Equal(RiskLevel.AtRisk, assessment.Level);
        Assert.Equal("next_attempt_after_deadline", assessment.ReasonCode);
    }

    [Fact]
    public void WorkedExampleOne_BackoffCannotFitBeforeDeadline()
    {
        // Attempt 1 of 4 has failed. Remaining un-jittered backoff is 30 + 60 + 120 = 210s.
        // Only 120s of slack remain, so even a perfect run overshoots.
        var assessment = RiskEvaluator.Evaluate(
            Job(JobState.RetryScheduled, deadlineIn: TimeSpan.FromSeconds(120), attempts: 1, maxAttempts: 4,
                nextAttemptIn: TimeSpan.FromSeconds(30)),
            Context(thresholdMinutes: 1));

        Assert.Equal(RiskLevel.AtRisk, assessment.Level);
        Assert.Equal("backoff_exceeds_slack", assessment.ReasonCode);
    }

    [Fact]
    public void WorkedExampleTwo_PlentyOfSlackIsOnTrack()
    {
        var assessment = RiskEvaluator.Evaluate(
            Job(deadlineIn: TimeSpan.FromHours(4)), Context(thresholdMinutes: 45));

        Assert.Equal(RiskLevel.OnTrack, assessment.Level);
        Assert.Equal("on_track", assessment.ReasonCode);
        Assert.Equal(4 * 3600, assessment.SecondsToDeadline);
    }

    [Fact]
    public void AFreshJobIsNotPenalisedForBackoffItHasNotNeededYet()
    {
        // attempts == 0, so the remaining-backoff rule does not apply.
        var assessment = RiskEvaluator.Evaluate(
            Job(deadlineIn: TimeSpan.FromMinutes(90), attempts: 0), Context(thresholdMinutes: 45));

        Assert.Equal(RiskLevel.OnTrack, assessment.Level);
    }

    [Fact]
    public void TheAtRiskThresholdIsExpressedInSimulatedMinutes()
    {
        // 45 simulated minutes at scale 60 is 45 real seconds of slack.
        var job = Job(deadlineIn: TimeSpan.FromSeconds(40));

        Assert.Equal(RiskLevel.AtRisk, RiskEvaluator.Evaluate(job, Context(45, timeScale: 60)).Level);
        Assert.Equal(RiskLevel.OnTrack, RiskEvaluator.Evaluate(job, Context(0.1, timeScale: 60)).Level);
    }

    [Theory]
    [InlineData(RiskLevel.Breached, true)]
    [InlineData(RiskLevel.NeedsHuman, true)]
    [InlineData(RiskLevel.AtRisk, true)]
    [InlineData(RiskLevel.OnTrack, false)]
    [InlineData(RiskLevel.Done, false)]
    public void OnlyTheFirstThreeLevelsCountTowardsDollarsAtRisk(RiskLevel level, bool counts)
        => Assert.Equal(counts, RiskEvaluator.CountsAsAtRisk(level));

    [Fact]
    public void EveryRiskLevelIsReachable()
    {
        var reached = new[]
        {
            RiskEvaluator.Evaluate(Job(JobState.Succeeded), Context()).Level,
            RiskEvaluator.Evaluate(Job(JobState.RetryScheduled, deadlineIn: TimeSpan.FromMinutes(-1)), Context()).Level,
            RiskEvaluator.Evaluate(Job(JobState.DeadLettered, deadlineIn: TimeSpan.FromHours(3)), Context()).Level,
            RiskEvaluator.Evaluate(Job(deadlineIn: TimeSpan.FromMinutes(10)), Context()).Level,
            RiskEvaluator.Evaluate(Job(deadlineIn: TimeSpan.FromHours(8)), Context()).Level
        }.Distinct().ToArray();

        Assert.Equal(Enum.GetValues<RiskLevel>().Length, reached.Length);
    }
}
