namespace CycleGuard.Api.Domain;

/// <summary>The three synthetic job types CycleGuard knows how to run.</summary>
public enum JobType
{
    ClaimsBatchAdjudication,
    EncounterSubmission,
    PaymentRunDisbursement
}

/// <summary>Lifecycle states. Transitions are policed by <see cref="JobStateMachine"/>.</summary>
public enum JobState
{
    Queued,
    Running,
    RetryScheduled,
    Succeeded,
    DeadLettered,
    Cancelled
}

/// <summary>
/// Transient failures earn a retry with backoff. Permanent failures go straight to the
/// dead-letter table with no retries, because retrying cannot change the outcome.
/// </summary>
public enum FailureClass
{
    Transient,
    Permanent
}

/// <summary>Operational risk, ordered by how urgently a human needs to look.</summary>
public enum RiskLevel
{
    Breached,
    NeedsHuman,
    AtRisk,
    OnTrack,
    Done
}

/// <summary>How a single attempt ended.</summary>
public enum AttemptOutcome
{
    Succeeded,
    TransientFailure,
    PermanentFailure,
    LeaseExpired
}
