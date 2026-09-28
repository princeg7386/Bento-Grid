using System.ComponentModel.DataAnnotations.Schema;

namespace CycleGuard.Api.Domain;

/// <summary>
/// One unit of overnight work. All instants are stored as UTC ticks (long) so that the
/// single-statement atomic claim in <c>JobQueue</c> can compare them in raw SQL without
/// depending on any date/time text format.
/// </summary>
public class Job
{
    public long Id { get; set; }

    public JobType Type { get; set; }

    /// <summary>Synthetic JSON payload. Also carries the demo failure script.</summary>
    public string Payload { get; set; } = "{}";

    public JobState State { get; set; } = JobState.Queued;

    /// <summary>Attempts started so far. Incremented by the atomic claim itself.</summary>
    public int Attempts { get; set; }

    /// <summary>Total attempts allowed, resolved from global + per-job-type config at enqueue time.</summary>
    public int MaxAttempts { get; set; }

    public long DeadlineTicks { get; set; }

    /// <summary>Synthetic dollars at stake, in cents, to avoid floating point money.</summary>
    public long AmountAtStakeCents { get; set; }

    /// <summary>Unique per job. Survives requeue, which is what makes requeue safe.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// The key the downstream effect ledger enforces. Defaults to <see cref="IdempotencyKey"/>.
    /// The simulator deliberately points two jobs at one downstream key to show a real
    /// duplicate being blocked.
    /// </summary>
    public string DownstreamIdempotencyKey { get; set; } = string.Empty;

    public string DownstreamEndpoint { get; set; } = string.Empty;

    public long? NextAttemptTicks { get; set; }

    /// <summary>When the current worker's claim expires. Past-due leases are reclaimed.</summary>
    public long? LeaseExpiresTicks { get; set; }

    public string? ClaimedBy { get; set; }

    public string? LastFailureSignature { get; set; }

    public FailureClass? LastFailureClass { get; set; }

    /// <summary>Masked before it ever reaches this column. Never holds raw PHI.</summary>
    public string? LastErrorMasked { get; set; }

    /// <summary>Masked before it ever reaches this column. Never holds raw PHI.</summary>
    public string? LastStackTraceMasked { get; set; }

    /// <summary>Set when an analyst requeues or explicitly closes a dead-lettered job.</summary>
    public bool DeadLetterResolved { get; set; }

    public int RequeueCount { get; set; }

    public long CreatedTicks { get; set; }

    public long UpdatedTicks { get; set; }

    public long? CompletedTicks { get; set; }

    /// <summary>True for jobs created by the demo simulator.</summary>
    public bool IsSynthetic { get; set; }

    public List<JobAttempt> AttemptLog { get; set; } = new();

    [NotMapped]
    public DateTime DeadlineUtc => Clock.ToUtc(DeadlineTicks);

    [NotMapped]
    public DateTime? NextAttemptUtc => NextAttemptTicks is null ? null : Clock.ToUtc(NextAttemptTicks.Value);

    [NotMapped]
    public DateTime? LeaseExpiresUtc => LeaseExpiresTicks is null ? null : Clock.ToUtc(LeaseExpiresTicks.Value);

    [NotMapped]
    public bool IsTerminal => State is JobState.Succeeded or JobState.Cancelled;

    /// <summary>Retries still permitted after the attempts already made.</summary>
    [NotMapped]
    public int RetriesRemaining => Math.Max(0, MaxAttempts - Attempts);
}
