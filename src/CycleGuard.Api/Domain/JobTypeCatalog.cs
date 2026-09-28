namespace CycleGuard.Api.Domain;

/// <summary>
/// The synthetic job types and the downstream endpoints they talk to. Invented for this
/// project; no real payer, state system or gateway is represented here.
/// </summary>
public static class JobTypeCatalog
{
    public const string ClaimsEngine = "claims-engine";
    public const string StateAMmis = "state-a-mmis";
    public const string StateBMmis = "state-b-mmis";
    public const string AchGateway = "ach-gateway";

    public static readonly string[] AllEndpoints = [ClaimsEngine, StateAMmis, StateBMmis, AchGateway];

    /// <summary>Endpoints a given job type is allowed to target.</summary>
    public static string[] EndpointsFor(JobType type) => type switch
    {
        JobType.ClaimsBatchAdjudication => [ClaimsEngine],
        JobType.EncounterSubmission => [StateAMmis, StateBMmis],
        JobType.PaymentRunDisbursement => [AchGateway],
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown job type.")
    };

    public static string DefaultEndpointFor(JobType type) => EndpointsFor(type)[0];

    /// <summary>True when finishing this job moves money and therefore needs ledger protection.</summary>
    public static bool MovesMoney(JobType type) => type == JobType.PaymentRunDisbursement;
}
