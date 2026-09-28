namespace CycleGuard.Api.Domain;

/// <summary>
/// The mock downstream's record of an effect that actually happened. The UNIQUE index on
/// <see cref="IdempotencyKey"/> is what makes a retried or requeued payment unable to
/// disburse twice: the insert is ON CONFLICT DO NOTHING and zero rows affected means
/// "already paid".
/// </summary>
public class DownstreamLedgerEntry
{
    public long Id { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    public long JobId { get; set; }

    public long AmountCents { get; set; }

    public long CreatedTicks { get; set; }
}
