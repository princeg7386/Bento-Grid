namespace CycleGuard.Api.Domain;

/// <summary>Thrown when code asks for a transition the table does not allow.</summary>
public sealed class IllegalStateTransitionException : InvalidOperationException
{
    public IllegalStateTransitionException(JobState from, JobState to, long jobId)
        : base($"Illegal state transition for job {jobId}: {from} -> {to}.")
    {
        From = from;
        To = to;
        JobId = jobId;
    }

    public JobState From { get; }

    public JobState To { get; }

    public long JobId { get; }
}

/// <summary>
/// The transition table. Every state change in CycleGuard goes through here, so an
/// illegal transition is a thrown exception rather than a silently corrupt row.
/// </summary>
public static class JobStateMachine
{
    private static readonly Dictionary<JobState, JobState[]> Allowed = new()
    {
        // A queued job can only start running, or be cancelled before it does.
        [JobState.Queued] = [JobState.Running, JobState.Cancelled],

        // A running job can finish, earn a retry, die permanently, or lose its lease
        // and fall back to Queued for another worker to pick up.
        [JobState.Running] =
        [
            JobState.Succeeded,
            JobState.RetryScheduled,
            JobState.DeadLettered,
            JobState.Queued,
            JobState.Cancelled
        ],

        // A scheduled retry runs when its time comes. It can also be dead-lettered
        // administratively (e.g. its deadline passed and waiting is pointless).
        [JobState.RetryScheduled] = [JobState.Running, JobState.DeadLettered, JobState.Cancelled],

        // Terminal.
        [JobState.Succeeded] = [],

        // Dead letters are re-openable by an analyst requeue, which puts them back in the queue.
        [JobState.DeadLettered] = [JobState.Queued, JobState.Cancelled],

        // Terminal.
        [JobState.Cancelled] = []
    };

    public static IReadOnlyDictionary<JobState, JobState[]> Table => Allowed;

    public static bool CanTransition(JobState from, JobState to)
        => Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Throws <see cref="IllegalStateTransitionException"/> if the move is not allowed.</summary>
    public static void EnsureCanTransition(JobState from, JobState to, long jobId)
    {
        if (!CanTransition(from, to))
        {
            throw new IllegalStateTransitionException(from, to, jobId);
        }
    }

    /// <summary>States a worker is allowed to claim from.</summary>
    public static readonly JobState[] Claimable = [JobState.Queued, JobState.RetryScheduled];
}
