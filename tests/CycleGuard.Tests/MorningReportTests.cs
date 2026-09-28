using CycleGuard.Api.Api;

using System.Globalization;

namespace CycleGuard.Tests;

/// <summary>
/// The verdict rules, tested against hand-built <see cref="StatusDto"/>/<see cref="GroupDto"/>
/// values rather than a live queue -- <see cref="MorningReportEvaluator"/> is a pure function,
/// so it does not need one.
/// </summary>
public class MorningReportTests
{
    private static readonly DateTime Now = new(2026, 3, 14, 8, 45, 0, DateTimeKind.Utc);

    private static StatusDto Status(
        bool scenarioLoaded = true,
        int breached = 0,
        int needsHuman = 0,
        int atRisk = 0,
        int onTrack = 0,
        int done = 0,
        int succeeded = 0,
        int total = 0,
        int deadLetters = 0,
        long dollarsAtRiskCents = 0,
        int duplicatesPrevented = 0,
        long duplicatesPreventedCents = 0,
        IReadOnlyList<OutageDto>? outages = null)
        => new(
            scenarioLoaded,
            scenarioLoaded ? Now.AddHours(4) : null,
            14400,
            14400,
            1,
            total,
            new Dictionary<string, int> { ["Succeeded"] = succeeded, ["DeadLettered"] = deadLetters },
            new Dictionary<string, int>
            {
                ["Breached"] = breached,
                ["NeedsHuman"] = needsHuman,
                ["AtRisk"] = atRisk,
                ["OnTrack"] = onTrack,
                ["Done"] = done
            },
            breached + needsHuman + atRisk,
            dollarsAtRiskCents,
            0,
            deadLetters,
            duplicatesPrevented,
            duplicatesPreventedCents,
            0,
            0,
            0,
            4,
            outages ?? []);

    [Fact]
    public void NoScenarioIsIdleNotHealthy()
    {
        var report = MorningReportEvaluator.Evaluate(Status(scenarioLoaded: false), [], Now, 30);

        Assert.Equal(nameof(HealthVerdict.Idle), report.Verdict);
        Assert.Contains("Simulate last night", report.Summary);
    }

    [Fact]
    public void AnySingleBreachIsCriticalEvenWithNothingElseWrong()
    {
        var report = MorningReportEvaluator.Evaluate(Status(breached: 1, total: 300, succeeded: 299), [], Now, 30);

        Assert.Equal(nameof(HealthVerdict.Critical), report.Verdict);
        Assert.Contains("1 job already missed its deadline", report.Headline);
    }

    [Fact]
    public void BreachedOutranksEverythingElse()
    {
        var report = MorningReportEvaluator.Evaluate(
            Status(breached: 2, needsHuman: 8, atRisk: 25, outages: [new OutageDto("state-b-mmis", Now.AddMinutes(5), 300)]),
            [],
            Now,
            30);

        Assert.Equal(nameof(HealthVerdict.Critical), report.Verdict);
    }

    [Fact]
    public void UnresolvedDeadLettersNeedAttentionWithNoBreach()
    {
        var report = MorningReportEvaluator.Evaluate(Status(needsHuman: 8, deadLetters: 8), [], Now, 30);

        Assert.Equal(nameof(HealthVerdict.NeedsAttention), report.Verdict);
        Assert.Contains("8 dead letters unresolved", report.Headline);
    }

    [Fact]
    public void AtRiskAloneNeedsAttention()
    {
        var report = MorningReportEvaluator.Evaluate(Status(atRisk: 3), [], Now, 30);
        Assert.Equal(nameof(HealthVerdict.NeedsAttention), report.Verdict);
    }

    [Fact]
    public void AnActiveOutageNeedsAttentionEvenWithNoAtRiskJobsYet()
    {
        var report = MorningReportEvaluator.Evaluate(
            Status(outages: [new OutageDto("state-b-mmis", Now.AddSeconds(20), 20)]),
            [],
            Now,
            30);

        Assert.Equal(nameof(HealthVerdict.NeedsAttention), report.Verdict);
        Assert.Contains("1 endpoint down", report.Headline);
    }

    [Fact]
    public void NothingWrongIsHealthy()
    {
        var report = MorningReportEvaluator.Evaluate(
            Status(onTrack: 10, done: 290, total: 300, succeeded: 290),
            [],
            Now,
            30);

        Assert.Equal(nameof(HealthVerdict.Healthy), report.Verdict);
        Assert.Equal("Nothing needs attention. Everything on track or done.", report.Headline);
    }

    [Fact]
    public void EveryVerdictIsReachable()
    {
        var verdicts = new[]
        {
            MorningReportEvaluator.Evaluate(Status(scenarioLoaded: false), [], Now, 30).Verdict,
            MorningReportEvaluator.Evaluate(Status(breached: 1), [], Now, 30).Verdict,
            MorningReportEvaluator.Evaluate(Status(needsHuman: 1), [], Now, 30).Verdict,
            MorningReportEvaluator.Evaluate(Status(), [], Now, 30).Verdict
        }.Distinct().ToArray();

        Assert.Equal(Enum.GetValues<HealthVerdict>().Length, verdicts.Length);
    }

    [Fact]
    public void TopIssuesAreSortedByDollarsAndCappedAtFive()
    {
        var groups = Enumerable.Range(1, 8)
            .Select(i => new GroupDto(
                "ach-gateway", $"cause-{i}", "Transient", "cause", "action",
                1, i * 1000L, Now.AddMinutes(i), i * 60, 0, 0, false))
            .ToList();

        var report = MorningReportEvaluator.Evaluate(Status(), groups, Now, 30);

        Assert.Equal(5, report.TopIssues.Count);
        Assert.Equal(8000L, report.TopIssues[0].DollarsAtRiskCents);
        Assert.True(report.TopIssues.Zip(report.TopIssues.Skip(1))
            .All(pair => pair.First.DollarsAtRiskCents >= pair.Second.DollarsAtRiskCents));
    }

    [Fact]
    public void SummaryNamesDuplicatesPreventedWhenAnyOccurred()
    {
        var report = MorningReportEvaluator.Evaluate(
            Status(duplicatesPrevented: 1, duplicatesPreventedCents: 441290),
            [],
            Now,
            30);

        Assert.Contains("blocked 1 duplicate disbursement", report.Summary);
        Assert.Contains("$4,412.90", report.Summary);
    }

    /// <summary>
    /// Dollar figures are formatted with an explicit culture rather than whatever the host
    /// machine defaults to. Without it this passed on a US-configured machine and printed
    /// "$1,99,690.55" (Indian-style grouping) on one that was not -- see docs/WHAT_BROKE.md.
    /// This test only proves anything because it deliberately runs under a culture that
    /// groups differently from en-US.
    /// </summary>
    [Fact]
    public void DollarFormattingIsCultureIndependent()
    {
        const long atRiskCents = 19_969_055;   // $199,690.55 -- 4 digits ahead of the last
                                                // group, where Indian and US grouping diverge.
        const long duplicatesCents = 150_412_900; // $1,504,129.00 -- same, at a different magnitude.

        // Derive what en-IN grouping actually produces rather than hand-writing it: that is
        // exactly the mistake that shipped originally (see docs/WHAT_BROKE.md) -- an
        // eyeballed "wrong" string that quietly wasn't wrong for the number chosen.
        var indianAtRisk = (atRiskCents / 100.0).ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
        var indianDuplicates = (duplicatesCents / 100.0).ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
        Assert.NotEqual(
            (atRiskCents / 100.0).ToString("N2", CultureInfo.InvariantCulture),
            indianAtRisk); // sanity check: this pair must actually diverge, or the test proves nothing.

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-IN");

            var report = MorningReportEvaluator.Evaluate(
                Status(atRisk: 1, dollarsAtRiskCents: atRiskCents, duplicatesPrevented: 1, duplicatesPreventedCents: duplicatesCents),
                [],
                Now,
                30);

            Assert.Contains("$199,690.55", report.Summary);
            Assert.Contains("$1,504,129.00", report.Summary);
            Assert.DoesNotContain(indianAtRisk, report.Summary);
            Assert.DoesNotContain(indianDuplicates, report.Summary);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void NextCheckIsExactlyOneIntervalAfterNow()
    {
        var report = MorningReportEvaluator.Evaluate(Status(), [], Now, 45);

        Assert.Equal(Now, report.GeneratedAtUtc);
        Assert.Equal(Now.AddSeconds(45), report.NextCheckUtc);
        Assert.Equal(45, report.CheckIntervalRealSeconds);
    }
}
