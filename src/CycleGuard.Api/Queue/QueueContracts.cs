using CycleGuard.Api.Domain;

namespace CycleGuard.Api.Queue;

/// <summary>A request to put work on the queue.</summary>
public sealed record EnqueueRequest(
    JobType Type,
    string Payload,
    DateTime DeadlineUtc,
    long AmountAtStakeCents,
    string? IdempotencyKey = null,
    string? DownstreamEndpoint = null,
    string? DownstreamIdempotencyKey = null,
    bool IsSynthetic = false,
    int? MaxAttempts = null);

public sealed record EnqueueResult(Job? Job, bool DuplicateKey, string? Error)
{
    public static EnqueueResult Ok(Job job) => new(job, false, null);

    public static EnqueueResult Duplicate(string key) => new(null, true, $"A job already exists with idempotency key '{key}'.");

    public static EnqueueResult Invalid(string error) => new(null, false, error);
}

/// <summary>What the downstream simulator decided happened on one attempt.</summary>
public sealed record ExecutionOutcome(
    bool Success,
    FailureClass? Class = null,
    string? Signature = null,
    string? RawMessage = null,
    string? RawStackTrace = null)
{
    public static ExecutionOutcome Succeeded() => new(true);

    public static ExecutionOutcome Failed(FailureClass failureClass, string signature, string rawMessage, string rawStackTrace)
        => new(false, failureClass, signature, rawMessage, rawStackTrace);
}

/// <summary>Result of committing a successful attempt.</summary>
public sealed record CompletionResult(bool Committed, bool DuplicatePrevented, string? Reason);

public enum RequeueOutcome
{
    Requeued,
    NotFound,
    NotDeadLettered,
    MissingAnalystDetails
}

public sealed record RequeueResult(RequeueOutcome Outcome, Job? Job)
{
    public bool Success => Outcome == RequeueOutcome.Requeued;
}

/// <summary>One job whose lease expired and was handed back to the queue.</summary>
public sealed record ReclaimedJob(long JobId, int AttemptNumber, string? PreviousWorker);
