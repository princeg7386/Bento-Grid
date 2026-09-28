namespace CycleGuard.Api.Domain;

/// <summary>Resolved retry policy for one job type.</summary>
public sealed record BackoffPolicy(
    int BaseSeconds,
    int CapSeconds,
    int MaxRetries,
    bool JitterEnabled = false,
    double JitterFraction = 0.0);

/// <summary>
/// delay = min(cap, base * 2^(attempt-1)), optionally jittered.
/// Jitter is seeded from (jobId, attempt) so a demo run is byte-for-byte repeatable.
/// </summary>
public static class BackoffCalculator
{
    /// <summary>
    /// Backoff after <paramref name="attempt"/> has failed. <paramref name="attempt"/> is
    /// 1-based: the first failure asks for base seconds.
    /// </summary>
    public static int DelaySeconds(BackoffPolicy policy, int attempt)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt numbers are 1-based.");
        }

        // Computed in double so a large attempt number saturates at the cap instead of
        // overflowing a 32-bit shift.
        var raw = policy.BaseSeconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(policy.CapSeconds, raw);
        return (int)Math.Round(Math.Max(1, capped), MidpointRounding.AwayFromZero);
    }

    /// <summary>Deterministic jittered delay. Same job + same attempt always gives the same answer.</summary>
    public static int DelaySecondsWithJitter(BackoffPolicy policy, int attempt, long jobId)
    {
        var baseDelay = DelaySeconds(policy, attempt);
        if (!policy.JitterEnabled || policy.JitterFraction <= 0)
        {
            return baseDelay;
        }

        var rng = new Random(HashCode.Combine(jobId, attempt));
        var swing = (rng.NextDouble() * 2.0) - 1.0; // -1 .. +1
        var jittered = baseDelay * (1.0 + (swing * policy.JitterFraction));
        var clamped = Math.Clamp(jittered, 1, policy.CapSeconds);
        return (int)Math.Round(clamped, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Total un-jittered backoff still ahead of a job that has made
    /// <paramref name="attemptsSoFar"/> attempts. Used by the risk model to answer
    /// "even if every remaining retry works, can it finish before the deadline?".
    /// </summary>
    public static long RemainingBackoffSeconds(BackoffPolicy policy, int attemptsSoFar, int maxAttempts)
    {
        long total = 0;
        for (var attempt = Math.Max(1, attemptsSoFar); attempt < maxAttempts; attempt++)
        {
            total += DelaySeconds(policy, attempt);
        }

        return total;
    }

    /// <summary>The upcoming schedule shown in the job drawer.</summary>
    public static IReadOnlyList<(int Attempt, int DelaySeconds)> UpcomingSchedule(
        BackoffPolicy policy,
        int attemptsSoFar,
        int maxAttempts,
        long jobId)
    {
        var schedule = new List<(int, int)>();
        for (var attempt = Math.Max(1, attemptsSoFar); attempt < maxAttempts; attempt++)
        {
            schedule.Add((attempt + 1, DelaySecondsWithJitter(policy, attempt, jobId)));
        }

        return schedule;
    }
}
