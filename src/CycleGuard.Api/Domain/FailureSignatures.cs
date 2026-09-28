namespace CycleGuard.Api.Domain;

/// <summary>
/// The stable failure signatures CycleGuard groups and explains by. A signature is the
/// normalised identity of a failure, independent of the message text (which may carry PHI
/// and gets masked).
/// </summary>
public static class FailureSignatures
{
    public const string DownstreamTimeout = "downstream_timeout";
    public const string Http503 = "http_503";
    public const string EndpointOutage = "endpoint_outage";
    public const string DbLockContention = "db_lock_contention";
    public const string LeaseExpired = "lease_expired";

    public const string ValidationMissingMemberId = "validation_missing_member_id";
    public const string ValidationUnknownProviderId = "validation_unknown_provider_id";
    public const string ValidationInvalidProcedureCode = "validation_invalid_procedure_code";
    public const string ValidationNegativeAmount = "validation_negative_amount";
    public const string MemberNotEnrolled = "member_not_enrolled";
    public const string AchAccountClosed = "ach_account_closed";
    public const string AchDailyLimitExceeded = "ach_daily_limit_exceeded";
    public const string DuplicateClaimId = "duplicate_claim_id";

    public const string Unknown = "unknown";
}
