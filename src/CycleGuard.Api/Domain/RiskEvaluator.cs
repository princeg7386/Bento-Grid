namespace CycleGuard.Api.Domain;

/// <summary>Why a job landed in the risk level it did, in one machine-readable token plus prose.</summary>
public sealed record RiskAssessment(
    RiskLevel Level,
    long SecondsToDeadline,
    string ReasonCode,
    string Reason);

/// <summary>Inputs the risk model needs beyond the job itself.</summary>
public sealed record RiskContext(
    DateTime NowUtc,
    double AtRiskThresholdSimulatedMinutes,
    double TimeScale,
    BackoffPolicy Policy)
{
    /// <summary>
    /// The threshold is expressed in the simulated minutes the analyst sees. Because the
    /// demo compresses time, the equivalent real-time window is divided by the time scale.
    /// </summary>
    public double AtRiskThresholdRealSeconds
        => AtRiskThresholdSimulatedMinutes * 60.0 / Math.Max(1.0, TimeScale);
}

/// <summary>
/// The whole point of CycleGuard: sort by when a job will cost money, not by when it broke.
/// Rules and worked examples live in docs/risk-model.md and are mirrored by the tests.
/// </summary>
public static class RiskEvaluator
{
    public static RiskAssessment Evaluate(Job job, RiskContext context)
    {
        var nowTicks = Clock.Ticks(context.NowUtc);
        var secondsToDeadline = (long)Math.Round((job.DeadlineTicks - nowTicks) / (double)TimeSpan.TicksPerSecond);

        // 1. Finished work is done, whatever the clock says.
        if (job.State == JobState.Succeeded)
        {
            return new RiskAssessment(RiskLevel.Done, secondsToDeadline, "succeeded", "Completed successfully.");
        }

        // 2. A cancelled job is nobody's problem. Deliberately not counted as Breached:
        //    an analyst already decided it should not run. Documented in docs/risk-model.md.
        if (job.State == JobState.Cancelled)
        {
            return new RiskAssessment(RiskLevel.Done, secondsToDeadline, "cancelled", "Cancelled by an operator.");
        }

        // 3. Past the regulatory or cycle deadline and not succeeded. Money is already exposed.
        if (nowTicks > job.DeadlineTicks)
        {
            return new RiskAssessment(
                RiskLevel.Breached,
                secondsToDeadline,
                "past_deadline",
                $"Deadline passed {FormatSpan(-secondsToDeadline)} ago and the job has not succeeded.");
        }

        // 4. Dead-lettered and nobody has dealt with it. Retrying will not help; a person must act.
        if (job.State == JobState.DeadLettered && !job.DeadLetterResolved)
        {
            return new RiskAssessment(
                RiskLevel.NeedsHuman,
                secondsToDeadline,
                "dead_lettered",
                "Dead-lettered and unresolved. Needs an analyst decision before the deadline.");
        }

        // 5a. Close enough to the deadline that it deserves attention now.
        if (secondsToDeadline <= context.AtRiskThresholdRealSeconds)
        {
            return new RiskAssessment(
                RiskLevel.AtRisk,
                secondsToDeadline,
                "deadline_near",
                $"Only {FormatSpan(secondsToDeadline)} of slack left before the deadline.");
        }

        // 5b. The next scheduled attempt is itself after the deadline, so the retry is pointless.
        if (job.NextAttemptTicks is { } next && next > job.DeadlineTicks)
        {
            return new RiskAssessment(
                RiskLevel.AtRisk,
                secondsToDeadline,
                "next_attempt_after_deadline",
                "The next retry is scheduled after the deadline, so it cannot land in time.");
        }

        // 5c. Even a perfect run through the remaining backoff schedule overshoots the deadline.
        var remainingBackoff = BackoffCalculator.RemainingBackoffSeconds(context.Policy, job.Attempts, job.MaxAttempts);
        if (job.Attempts > 0 && remainingBackoff > secondsToDeadline)
        {
            return new RiskAssessment(
                RiskLevel.AtRisk,
                secondsToDeadline,
                "backoff_exceeds_slack",
                $"Remaining backoff needs {FormatSpan(remainingBackoff)} but only {FormatSpan(secondsToDeadline)} remain.");
        }

        // 6. Nothing alarming.
        return new RiskAssessment(RiskLevel.OnTrack, secondsToDeadline, "on_track", "Inside its deadline with slack to spare.");
    }

    /// <summary>Risk levels that put a job on the "needs attention" tally and the dollars-at-risk total.</summary>
    public static bool CountsAsAtRisk(RiskLevel level)
        => level is RiskLevel.Breached or RiskLevel.NeedsHuman or RiskLevel.AtRisk;

    private static string FormatSpan(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Abs(seconds));
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }
}
