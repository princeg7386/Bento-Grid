using System.Globalization;

namespace CycleGuard.Api.Api;

/// <summary>
/// The three verdicts a human (or a pager) can act on without reading a single row of raw
/// data. <see cref="Idle"/> is not a failure state -- it means no scenario has been seeded, so
/// there is nothing to check yet.
/// </summary>
public enum HealthVerdict
{
    Idle,
    Healthy,
    NeedsAttention,
    Critical
}

/// <summary>
/// What "check this one endpoint at 8:45am" resolves to. Everything in it is derived from
/// <see cref="StatusDto"/> and <see cref="GroupDto"/>, which is why
/// <see cref="MorningReportEvaluator"/> can be unit tested without a database.
/// </summary>
public sealed record MorningReportDto(
    DateTime GeneratedAtUtc,
    DateTime NextCheckUtc,
    int CheckIntervalRealSeconds,
    string Verdict,
    string Headline,
    string Summary,
    int TotalJobs,
    int SucceededCount,
    int BreachedCount,
    int NeedsHumanCount,
    int AtRiskCount,
    int DeadLetterCount,
    long DollarsAtRiskCents,
    int DuplicatesPreventedCount,
    long DuplicatesPreventedCents,
    IReadOnlyList<GroupDto> TopIssues,
    IReadOnlyList<OutageDto> ActiveOutages);

/// <summary>
/// Deterministic rules, no AI, matching ADR-4 for every other decision CycleGuard makes: the
/// same status and groups always produce the same verdict. Order matters -- first match wins,
/// same convention as <c>RiskEvaluator</c> (see docs/risk-model.md).
/// </summary>
public static class MorningReportEvaluator
{
    public static MorningReportDto Evaluate(
        StatusDto status,
        IReadOnlyList<GroupDto> groups,
        DateTime nowUtc,
        int checkIntervalRealSeconds)
    {
        var breached = status.ByRisk.GetValueOrDefault(nameof(Domain.RiskLevel.Breached));
        var needsHuman = status.ByRisk.GetValueOrDefault(nameof(Domain.RiskLevel.NeedsHuman));
        var atRisk = status.ByRisk.GetValueOrDefault(nameof(Domain.RiskLevel.AtRisk));
        var succeeded = status.ByState.GetValueOrDefault(nameof(Domain.JobState.Succeeded));

        var topIssues = groups
            .OrderByDescending(g => g.DollarsAtRiskCents)
            .Take(5)
            .ToList();

        var (verdict, headline) = Classify(status, breached, needsHuman, atRisk);
        var summary = Summarize(status, breached, needsHuman, atRisk, succeeded);

        return new MorningReportDto(
            nowUtc,
            nowUtc.AddSeconds(checkIntervalRealSeconds),
            checkIntervalRealSeconds,
            verdict.ToString(),
            headline,
            summary,
            status.TotalJobs,
            succeeded,
            breached,
            needsHuman,
            atRisk,
            status.DeadLetterCount,
            status.DollarsAtRiskCents,
            status.DuplicatesPreventedCount,
            status.DuplicatesPreventedCents,
            topIssues,
            status.ActiveOutages);
    }

    private static (HealthVerdict, string) Classify(StatusDto status, int breached, int needsHuman, int atRisk)
    {
        // 1. Nothing has ever been seeded (or it was just reset): there is nothing to check.
        //    This is deliberately not "Healthy" -- a demo that has never run should not read as
        //    a clean bill of health.
        if (!status.ScenarioLoaded)
        {
            return (HealthVerdict.Idle, "No cycle is loaded. Nothing has run yet.");
        }

        // 2. Money has already missed a deadline. This is the only verdict that means "act now,
        //    before you finish reading this sentence" -- see docs/risk-model.md for what
        //    Breached means and why it outranks everything else.
        if (breached > 0)
        {
            return (HealthVerdict.Critical,
                $"{breached} job{(breached == 1 ? "" : "s")} already missed its deadline.");
        }

        // 3. Nothing is breached yet, but something needs a person: an unresolved dead letter,
        //    a job whose arithmetic says it will not make it, or a downstream endpoint that is
        //    still down.
        if (needsHuman > 0 || atRisk > 0 || status.ActiveOutages.Count > 0)
        {
            var parts = new List<string>();
            if (needsHuman > 0) parts.Add($"{needsHuman} dead letter{(needsHuman == 1 ? "" : "s")} unresolved");
            if (atRisk > 0) parts.Add($"{atRisk} job{(atRisk == 1 ? "" : "s")} at risk");
            if (status.ActiveOutages.Count > 0)
            {
                parts.Add($"{status.ActiveOutages.Count} endpoint{(status.ActiveOutages.Count == 1 ? "" : "s")} down");
            }

            return (HealthVerdict.NeedsAttention, string.Join(", ", parts) + ".");
        }

        // 4. Nothing breached, nothing parked, nothing stalled.
        return (HealthVerdict.Healthy, "Nothing needs attention. Everything on track or done.");
    }

    private static string Summarize(StatusDto status, int breached, int needsHuman, int atRisk, int succeeded)
    {
        var money = status.DollarsAtRiskCents / 100.0;
        var basics =
            $"{status.TotalJobs} jobs total, {succeeded} succeeded, {status.DeadLetterCount} dead-lettered.";

        if (!status.ScenarioLoaded)
        {
            return $"{basics} Run \"Simulate last night\" or POST /api/demo/scenarios/last-night to load one.";
        }

        // string interpolation formats numbers with CultureInfo.CurrentCulture, which is
        // whatever region the host OS is set to -- on the machine this was built on that is
        // "," as the thousands separator every three digits from the right *except* the
        // first group, e.g. "$1,99,690.55" instead of "$199,690.55". Formatting explicitly
        // with InvariantCulture (US-style grouping, matching the frontend's en-US formatter)
        // is the only way this endpoint reads the same on every machine that runs it.
        var risk = breached + needsHuman + atRisk == 0
            ? "Nothing is breached, parked or at risk."
            : $"{breached} breached, {needsHuman} needing a human, {atRisk} at risk, " +
              $"${money.ToString("N2", CultureInfo.InvariantCulture)} exposed.";

        var duplicates = status.DuplicatesPreventedCount > 0
            ? $" The ledger blocked {status.DuplicatesPreventedCount} duplicate disbursement(s) worth " +
              $"${(status.DuplicatesPreventedCents / 100.0).ToString("N2", CultureInfo.InvariantCulture)}."
            : string.Empty;

        return $"{basics} {risk}{duplicates}";
    }
}

/// <summary>
/// Holds the single most recent report the monitor computed. A viewer reading
/// <c>GET /api/morning-report</c> gets whatever was last written here -- the request never
/// triggers its own computation, which is what makes this "a job checks, not a person".
/// </summary>
public sealed class MorningReportStore
{
    private readonly object _gate = new();
    private MorningReportDto? _latest;

    public MorningReportDto? Latest
    {
        get { lock (_gate) { return _latest; } }
    }

    public void Set(MorningReportDto report)
    {
        lock (_gate) { _latest = report; }
    }
}
