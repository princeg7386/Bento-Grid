namespace CycleGuard.Api.Domain;

/// <summary>
/// Append-only audit trail. Every state change and every requeue writes one row.
/// Nothing in the application updates or deletes these.
/// </summary>
public class AuditEvent
{
    public long Id { get; set; }

    public long? JobId { get; set; }

    public long AtTicks { get; set; }

    /// <summary>"worker-2", "lease-reaper", "system", or an analyst's name for a requeue.</summary>
    public string Actor { get; set; } = "system";

    public string EventType { get; set; } = string.Empty;

    /// <summary>Human-readable detail. PHI-masked like everything else.</summary>
    public string Details { get; set; } = string.Empty;
}

/// <summary>Well-known audit event type names.</summary>
public static class AuditEventTypes
{
    public const string Enqueued = "Enqueued";
    public const string Claimed = "Claimed";
    public const string Succeeded = "Succeeded";
    public const string RetryScheduled = "RetryScheduled";
    public const string DeadLettered = "DeadLettered";
    public const string Cancelled = "Cancelled";
    public const string LeaseExpired = "LeaseExpired";
    public const string Requeued = "Requeued";
    public const string RequeueRejected = "RequeueRejected";
    public const string DuplicatePrevented = "DuplicateDisbursementPrevented";
    public const string DisbursementRecorded = "DisbursementRecorded";
    public const string ScenarioSeeded = "ScenarioSeeded";
    public const string ScenarioReset = "ScenarioReset";
    public const string WorkerKilled = "WorkerKilled";
}
