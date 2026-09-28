namespace CycleGuard.Api.Domain;

/// <summary>A plain-English explanation and the thing an analyst should actually do.</summary>
public sealed record CauseExplanation(
    string Signature,
    FailureClass Class,
    string Cause,
    string SuggestedAction);

/// <summary>
/// A deterministic lookup table from failure signature to plain English. No model, no
/// inference, no scoring: the same signature always produces the same sentence, which is
/// what makes the dashboard trustworthy at 8:45am. See ADR-4 in the README.
/// </summary>
public static class CauseRules
{
    private static readonly Dictionary<string, CauseExplanation> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        [FailureSignatures.DownstreamTimeout] = new(
            FailureSignatures.DownstreamTimeout,
            FailureClass.Transient,
            "The downstream system accepted the request but did not answer in time.",
            "No action needed. It retries automatically with backoff."),

        [FailureSignatures.Http503] = new(
            FailureSignatures.Http503,
            FailureClass.Transient,
            "The downstream system returned 503 Service Unavailable.",
            "No action needed while attempts remain. Escalate if it is still failing after the last retry."),

        [FailureSignatures.EndpointOutage] = new(
            FailureSignatures.EndpointOutage,
            FailureClass.Transient,
            "The endpoint is refusing all traffic, so every job pointed at it is stalled.",
            "Check the endpoint's status page. Queued work resumes on its own once the endpoint answers."),

        [FailureSignatures.DbLockContention] = new(
            FailureSignatures.DbLockContention,
            FailureClass.Transient,
            "The job could not get a write lock on the local queue database.",
            "No action needed. This clears itself; report it if it repeats under normal load."),

        [FailureSignatures.LeaseExpired] = new(
            FailureSignatures.LeaseExpired,
            FailureClass.Transient,
            "The worker holding this job stopped reporting, so its claim expired.",
            "No action needed. Another worker picks the job up and the attempt history is kept."),

        [FailureSignatures.ValidationMissingMemberId] = new(
            FailureSignatures.ValidationMissingMemberId,
            FailureClass.Permanent,
            "The submission has no member identifier, so the downstream system rejected it outright.",
            "Fix the member identifier in the source record, then requeue this job."),

        [FailureSignatures.ValidationUnknownProviderId] = new(
            FailureSignatures.ValidationUnknownProviderId,
            FailureClass.Permanent,
            "The provider identifier is not recognised by the state system.",
            "Confirm the provider is enrolled and active, correct the identifier, then requeue."),

        [FailureSignatures.ValidationInvalidProcedureCode] = new(
            FailureSignatures.ValidationInvalidProcedureCode,
            FailureClass.Permanent,
            "A procedure code on the claim is not valid for the service date.",
            "Send the claim back to coding, then requeue once the code is corrected."),

        [FailureSignatures.ValidationNegativeAmount] = new(
            FailureSignatures.ValidationNegativeAmount,
            FailureClass.Permanent,
            "The payment amount is zero or negative, which the gateway will never accept.",
            "Correct the amount upstream. Do not requeue until the amount is positive."),

        [FailureSignatures.MemberNotEnrolled] = new(
            FailureSignatures.MemberNotEnrolled,
            FailureClass.Permanent,
            "The member was not enrolled on the date of service.",
            "Verify eligibility for the service date. This usually needs an eligibility correction, not a retry."),

        [FailureSignatures.AchAccountClosed] = new(
            FailureSignatures.AchAccountClosed,
            FailureClass.Permanent,
            "The receiving bank account is closed, so the disbursement cannot settle.",
            "Get updated banking details from the provider, then requeue. The idempotency key stops a double payment."),

        [FailureSignatures.AchDailyLimitExceeded] = new(
            FailureSignatures.AchDailyLimitExceeded,
            FailureClass.Permanent,
            "The disbursement exceeds the gateway's daily limit for this originator.",
            "Split the run or raise the limit with the bank, then requeue. Retrying as-is will fail identically."),

        [FailureSignatures.DuplicateClaimId] = new(
            FailureSignatures.DuplicateClaimId,
            FailureClass.Permanent,
            "The downstream system already has a claim with this identifier.",
            "Confirm whether the original was accepted. If it was, cancel this job rather than requeueing it."),

        [FailureSignatures.Unknown] = new(
            FailureSignatures.Unknown,
            FailureClass.Transient,
            "The failure did not match any known signature.",
            "Read the masked error in the drawer. If it recurs, add a signature rule for it.")
    };

    public static CauseExplanation Explain(string? signature)
    {
        if (!string.IsNullOrWhiteSpace(signature) && Table.TryGetValue(signature, out var explanation))
        {
            return explanation;
        }

        return Table[FailureSignatures.Unknown];
    }

    /// <summary>The failure class the rule table says a signature belongs to.</summary>
    public static FailureClass ClassOf(string? signature) => Explain(signature).Class;

    public static IReadOnlyCollection<CauseExplanation> All => Table.Values;
}
