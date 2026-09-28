using CycleGuard.Api.Domain;

namespace CycleGuard.Api.Downstream;

/// <summary>
/// Builds the fake error text a mock endpoint returns. Messages deliberately contain
/// synthetic PHI-shaped values so the dashboard can show what masking removes. None of
/// these values belong to a real person.
/// </summary>
public static class SyntheticErrorText
{
    public static (string Message, string StackTrace) Compose(string signature, JobPayload payload, string endpoint)
    {
        var member = payload.Member ?? new SyntheticMember
        {
            Id = "MBR-0000000",
            Name = "Unknown Member",
            Dob = "1900-01-01",
            Phone = "000-555-0000",
            Email = "unknown@example.org",
            Ssn = "000-00-0000",
            ProviderId = "PRV-000000"
        };

        var message = signature switch
        {
            FailureSignatures.DownstreamTimeout =>
                $"Timeout after 30000ms calling {endpoint}/submit for batch {payload.BatchId} " +
                $"(MemberId: {member.Id}, Name: {member.Name}, DOB: {member.Dob}).",

            FailureSignatures.Http503 =>
                $"HTTP 503 Service Unavailable from {endpoint}/submit. Batch {payload.BatchId}, " +
                $"MemberId: {member.Id}, contact {member.Phone}.",

            FailureSignatures.EndpointOutage =>
                $"Connection refused by {endpoint}. Batch {payload.BatchId} could not be delivered " +
                $"(MemberId: {member.Id}).",

            FailureSignatures.ValidationMissingMemberId =>
                $"Validation failed at {endpoint}: field memberId is required. " +
                $"Submitted record had Name: {member.Name}, DOB: {member.Dob}, SSN: {member.Ssn}, " +
                $"contact {member.Email} / {member.Phone}.",

            FailureSignatures.ValidationUnknownProviderId =>
                $"Validation failed at {endpoint}: unknown provider id '{member.ProviderId}' for " +
                $"MemberId: {member.Id} (Name: {member.Name}, DOB: {member.Dob}).",

            FailureSignatures.ValidationInvalidProcedureCode =>
                $"Validation failed at {endpoint}: procedure code not valid for service date 03/14/2026. " +
                $"MemberId: {member.Id}, Name: {member.Name}.",

            FailureSignatures.ValidationNegativeAmount =>
                $"Validation failed at {endpoint}: disbursement amount must be greater than zero. " +
                $"Batch {payload.BatchId}, MemberId: {member.Id}.",

            FailureSignatures.MemberNotEnrolled =>
                $"Eligibility check failed at {endpoint}: MemberId: {member.Id} " +
                $"(Name: {member.Name}, DOB: {member.Dob}, SSN: {member.Ssn}) was not enrolled on 02/28/2026.",

            FailureSignatures.AchAccountClosed =>
                $"ACH return R02 from {endpoint}: account closed. Provider {member.ProviderId}, " +
                $"remit contact {member.Email}, phone {member.Phone}.",

            FailureSignatures.AchDailyLimitExceeded =>
                $"ACH reject from {endpoint}: originator daily limit exceeded for batch {payload.BatchId}. " +
                $"Provider {member.ProviderId}, contact {member.Phone}.",

            FailureSignatures.DuplicateClaimId =>
                $"Rejected by {endpoint}: claim id already present for MemberId: {member.Id} " +
                $"(Name: {member.Name}).",

            FailureSignatures.DbLockContention =>
                $"Could not acquire a write lock on the local queue while recording batch {payload.BatchId}.",

            _ => $"Unclassified failure from {endpoint} for batch {payload.BatchId} (MemberId: {member.Id})."
        };

        // Double-dollar raw string: interpolation is {{expr}}, so a literal brace stays a
        // single brace and the context block can look like the JSON these systems return.
        var stackTrace = $$"""
            CycleGuard.Downstream.{{EndpointClassName(endpoint)}}Client+SubmitFailure: {{message}}
               at CycleGuard.Downstream.{{EndpointClassName(endpoint)}}Client.SubmitAsync(Batch batch, MemberRecord member) in /src/downstream/{{endpoint}}/Client.cs:line 148
               at CycleGuard.Downstream.SubmissionPipeline.DispatchAsync(Batch batch) in /src/downstream/Pipeline.cs:line 62
               at CycleGuard.Workers.JobExecutor.ExecuteAsync(Job job, String workerId) in /src/workers/JobExecutor.cs:line 91
            --- context ---
               batch={{payload.BatchId}} endpoint={{endpoint}} memberCount={{payload.MemberCount}}
               member={ MemberId: {{member.Id}}, Name: {{member.Name}}, DOB: {{member.Dob}}, SSN: {{member.Ssn}}, phone={{member.Phone}}, email={{member.Email}} }
            """;

        return (message, stackTrace);
    }

    private static string EndpointClassName(string endpoint)
        => string.Concat(endpoint.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
