using System.Text.Json;
using System.Text.Json.Serialization;

namespace CycleGuard.Api.Downstream;

/// <summary>
/// The synthetic payload a job carries. Every value here is invented. The member block
/// exists so that simulated failures can contain realistic-looking PHI and the dashboard
/// can demonstrate masking on something other than a placeholder.
/// </summary>
public sealed class JobPayload
{
    public string BatchId { get; set; } = string.Empty;

    public int MemberCount { get; set; }

    public string? StateCode { get; set; }

    public SyntheticMember? Member { get; set; }

    public FailureScript? Script { get; set; }
}

/// <summary>Fake member details. Not derived from any real person or record.</summary>
public sealed class SyntheticMember
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Dob { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Ssn { get; set; } = string.Empty;

    public string ProviderId { get; set; } = string.Empty;
}

/// <summary>
/// Tells the simulator how this job should behave. Deterministic, so the same seeded
/// scenario always produces the same story.
/// </summary>
public sealed class FailureScript
{
    /// <summary>Attempts up to and including this number fail. 0 means never fail.</summary>
    public int FailUntilAttempt { get; set; }

    public string Signature { get; set; } = string.Empty;
}

public static class JobPayloadCodec
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialise(JobPayload payload) => JsonSerializer.Serialize(payload, Options);

    public static JobPayload Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JobPayload();
        }

        try
        {
            return JsonSerializer.Deserialize<JobPayload>(json, Options) ?? new JobPayload();
        }
        catch (JsonException)
        {
            // A malformed payload should not take a worker down; it just has no script.
            return new JobPayload();
        }
    }
}
