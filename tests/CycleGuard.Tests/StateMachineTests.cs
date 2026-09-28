using CycleGuard.Api.Domain;

namespace CycleGuard.Tests;

public class StateMachineTests
{
    [Theory]
    [InlineData(JobState.Queued, JobState.Running)]
    [InlineData(JobState.Queued, JobState.Cancelled)]
    [InlineData(JobState.Running, JobState.Succeeded)]
    [InlineData(JobState.Running, JobState.RetryScheduled)]
    [InlineData(JobState.Running, JobState.DeadLettered)]
    [InlineData(JobState.Running, JobState.Queued)]          // lease expiry hands it back
    [InlineData(JobState.RetryScheduled, JobState.Running)]
    [InlineData(JobState.RetryScheduled, JobState.DeadLettered)]
    [InlineData(JobState.DeadLettered, JobState.Queued)]     // analyst requeue
    public void LegalTransitionsAreAllowed(JobState from, JobState to)
        => Assert.True(JobStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(JobState.Queued, JobState.Succeeded)]         // must run first
    [InlineData(JobState.Queued, JobState.DeadLettered)]
    [InlineData(JobState.Queued, JobState.RetryScheduled)]
    [InlineData(JobState.Succeeded, JobState.Queued)]         // terminal
    [InlineData(JobState.Succeeded, JobState.Running)]
    [InlineData(JobState.Succeeded, JobState.DeadLettered)]
    [InlineData(JobState.Cancelled, JobState.Queued)]         // terminal
    [InlineData(JobState.Cancelled, JobState.Running)]
    [InlineData(JobState.DeadLettered, JobState.Succeeded)]   // cannot fake a success
    [InlineData(JobState.DeadLettered, JobState.Running)]     // must be requeued first
    [InlineData(JobState.RetryScheduled, JobState.Succeeded)]
    public void IllegalTransitionsAreRejected(JobState from, JobState to)
        => Assert.False(JobStateMachine.CanTransition(from, to));

    [Fact]
    public void EnsureCanTransitionThrowsWithBothStatesAndTheJobId()
    {
        var exception = Assert.Throws<IllegalStateTransitionException>(
            () => JobStateMachine.EnsureCanTransition(JobState.Succeeded, JobState.Running, jobId: 4242));

        Assert.Equal(JobState.Succeeded, exception.From);
        Assert.Equal(JobState.Running, exception.To);
        Assert.Equal(4242, exception.JobId);
        Assert.Contains("4242", exception.Message);
    }

    [Fact]
    public void EnsureCanTransitionIsSilentOnALegalMove()
        => JobStateMachine.EnsureCanTransition(JobState.Running, JobState.Succeeded, jobId: 1);

    [Fact]
    public void TerminalStatesHaveNoExits()
    {
        Assert.Empty(JobStateMachine.Table[JobState.Succeeded]);
        Assert.Empty(JobStateMachine.Table[JobState.Cancelled]);
    }

    [Fact]
    public void EveryStateAppearsInTheTable()
    {
        foreach (var state in Enum.GetValues<JobState>())
        {
            Assert.True(JobStateMachine.Table.ContainsKey(state), $"{state} is missing from the transition table.");
        }
    }

    [Fact]
    public void OnlyQueuedAndRetryScheduledAreClaimable()
        => Assert.Equal([JobState.Queued, JobState.RetryScheduled], JobStateMachine.Claimable);
}
