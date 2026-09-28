namespace CycleGuard.Api.Api;

/// <summary>A job as the risk-sorted list shows it.</summary>
public sealed record JobSummaryDto(
    long Id,
    string Type,
    string State,
    string Risk,
    string RiskReasonCode,
    string RiskReason,
    long SecondsToDeadline,
    long SimulatedSecondsToDeadline,
    DateTime DeadlineUtc,
    long AmountAtStakeCents,
    int Attempts,
    int MaxAttempts,
    string DownstreamEndpoint,
    long? SecondsToNextAttempt,
    long? SimulatedSecondsToNextAttempt,
    string? FailureSignature,
    string? FailureClass,
    string? Cause,
    string? SuggestedAction,
    string IdempotencyKey,
    int RequeueCount,
    bool DeadLetterResolved,
    bool IsSynthetic,
    string? SlaClass,
    string? SlaLabel);

public sealed record AttemptDto(
    long Id,
    int AttemptNumber,
    string WorkerId,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    string? Outcome,
    string? FailureSignature,
    string? FailureClass,
    string? ErrorMasked,
    string? StackTraceMasked,
    int? BackoffSeconds,
    DateTime? NextAttemptUtc,
    string? ErrorRaw,
    string? StackTraceRaw);

/// <summary>One upcoming retry in the backoff schedule the drawer counts down.</summary>
public sealed record BackoffSlotDto(
    int AttemptNumber,
    int SimulatedDelaySeconds,
    double RealDelaySeconds,
    DateTime? ProjectedRunUtc,
    bool AfterDeadline);

public sealed record AuditEventDto(
    long Id,
    DateTime AtUtc,
    string Actor,
    string EventType,
    string Details);

public sealed record JobDetailDto(
    JobSummaryDto Summary,
    string Payload,
    IReadOnlyList<AttemptDto> Attempts,
    IReadOnlyList<BackoffSlotDto> UpcomingBackoff,
    IReadOnlyList<AuditEventDto> Events,
    string? LastErrorMasked,
    string? LastStackTraceMasked,
    string? LastErrorRaw,
    string? LastStackTraceRaw,
    bool RawErrorsAvailable);

public sealed record StatusDto(
    bool ScenarioLoaded,
    DateTime? CycleCloseUtc,
    long SecondsToCycleClose,
    long SimulatedSecondsToCycleClose,
    double TimeScale,
    int TotalJobs,
    Dictionary<string, int> ByState,
    Dictionary<string, int> ByRisk,
    int NeedingAttention,
    long DollarsAtRiskCents,
    long DollarsCompletedCents,
    int DeadLetterCount,
    int DuplicatesPreventedCount,
    long DuplicatesPreventedCents,
    double ThroughputPerMinute,
    double SuccessRatePercent,
    double RetryRatePercent,
    int WorkerCount,
    IReadOnlyList<OutageDto> ActiveOutages,
    long ValueProtectedCents,
    int RecoveredFromCrashedWorkerCount,
    long RecoveredFromCrashedWorkerCents);

public sealed record OutageDto(string Endpoint, DateTime RecoversAtUtc, long SecondsRemaining);

/// <summary>Root-cause grouping by (endpoint, failure signature).</summary>
public sealed record GroupDto(
    string DownstreamEndpoint,
    string FailureSignature,
    string FailureClass,
    string Cause,
    string SuggestedAction,
    int JobCount,
    long DollarsAtRiskCents,
    DateTime? EarliestDeadlineUtc,
    long? SecondsToEarliestDeadline,
    int BreachedCount,
    int NeedsHumanCount,
    bool Healing);

public sealed record DeadLetterGroupDto(
    string FailureSignature,
    string Cause,
    string SuggestedAction,
    string FailureClass,
    int JobCount,
    long DollarsAtRiskCents,
    IReadOnlyList<JobSummaryDto> Jobs);

// ------------------------------------------------------------------ requests

public sealed record CreateJobRequest(
    string Type,
    string? Payload,
    DateTime DeadlineUtc,
    long AmountAtStakeCents,
    string? IdempotencyKey,
    string? DownstreamEndpoint,
    string? DownstreamIdempotencyKey,
    int? MaxAttempts);

public sealed record RequeueRequest(string Analyst, string Note);

public sealed record TimeScaleRequest(double TimeScale);
