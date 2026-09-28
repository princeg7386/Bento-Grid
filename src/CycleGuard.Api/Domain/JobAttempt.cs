namespace CycleGuard.Api.Domain;

/// <summary>One execution of a job, kept forever so requeue never loses history.</summary>
public class JobAttempt
{
    public long Id { get; set; }

    public long JobId { get; set; }

    public int AttemptNumber { get; set; }

    public string WorkerId { get; set; } = string.Empty;

    public long StartedTicks { get; set; }

    public long? FinishedTicks { get; set; }

    public AttemptOutcome? Outcome { get; set; }

    public string? FailureSignature { get; set; }

    public FailureClass? FailureClass { get; set; }

    /// <summary>Masked before persistence. Never holds raw PHI.</summary>
    public string? ErrorMasked { get; set; }

    /// <summary>Masked before persistence. Never holds raw PHI.</summary>
    public string? StackTraceMasked { get; set; }

    /// <summary>Backoff granted after this attempt, in real seconds actually waited.</summary>
    public int? BackoffSeconds { get; set; }

    public long? NextAttemptTicks { get; set; }

    public Job? Job { get; set; }
}
