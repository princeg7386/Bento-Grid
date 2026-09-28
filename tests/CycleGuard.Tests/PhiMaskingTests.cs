using CycleGuard.Api.Domain;
using CycleGuard.Api.Downstream;

namespace CycleGuard.Tests;

/// <summary>
/// Table-driven masking tests. Each case asserts both that the sensitive value is gone and
/// that a marker token replaced it, because a rule that deletes text silently is as bad as
/// one that leaks.
/// </summary>
public class PhiMaskingTests
{
    [Theory]
    // --- SSN-shaped -------------------------------------------------------------
    [InlineData("Rejected for member with SSN 912-55-1173.", "912-55-1173", PhiMasker.SsnToken)]
    [InlineData("SSN: 912-55-1173 not found", "912-55-1173", PhiMasker.SsnToken)]
    [InlineData("social_security_number=912551173 invalid", "912551173", PhiMasker.SsnToken)]
    // --- member identifiers -----------------------------------------------------
    [InlineData("MemberId: MBR-4471902 was not enrolled", "MBR-4471902", PhiMasker.MemberToken)]
    [InlineData("lookup failed for MBR-4471902", "MBR-4471902", PhiMasker.MemberToken)]
    [InlineData("member_id = M004471902 rejected", "M004471902", PhiMasker.MemberToken)]
    [InlineData("MID#88117402 unknown", "88117402", PhiMasker.MemberToken)]
    // --- dates of birth ---------------------------------------------------------
    [InlineData("DOB: 1974-03-02 mismatch", "1974-03-02", PhiMasker.DobToken)]
    [InlineData("DOB 03/02/1974 mismatch", "03/02/1974", PhiMasker.DobToken)]
    [InlineData("date_of_birth=1974-03-02", "1974-03-02", PhiMasker.DobToken)]
    [InlineData("BirthDate: 1961-11-29", "1961-11-29", PhiMasker.DobToken)]
    // --- other dates ------------------------------------------------------------
    [InlineData("service date 03/14/2026 not covered", "03/14/2026", PhiMasker.DateToken)]
    // --- phone numbers ----------------------------------------------------------
    [InlineData("call back on 602-555-0134", "602-555-0134", PhiMasker.PhoneToken)]
    [InlineData("contact (602) 555-0134 for remit", "(602) 555-0134", PhiMasker.PhoneToken)]
    [InlineData("phone +1 602 555 0134 unreachable", "602 555 0134", PhiMasker.PhoneToken)]
    // --- email ------------------------------------------------------------------
    [InlineData("bounced to d.whitfield42@example.org", "d.whitfield42@example.org", PhiMasker.EmailToken)]
    [InlineData("remit contact PROVIDER.Billing+ach@example.org failed", "PROVIDER.Billing+ach@example.org", PhiMasker.EmailToken)]
    // --- names ------------------------------------------------------------------
    [InlineData("record had Name: Dana Whitfield, plan=AZ", "Dana Whitfield", PhiMasker.NameToken)]
    [InlineData("PatientName = Marcus Okonjo", "Marcus Okonjo", PhiMasker.NameToken)]
    [InlineData("SubscriberName: \"Priya Ramanathan\" rejected", "Priya Ramanathan", PhiMasker.NameToken)]
    public void SensitiveValuesAreReplacedWithTheirToken(string input, string secret, string token)
    {
        var masked = PhiMasker.Mask(input);

        Assert.NotNull(masked);
        Assert.DoesNotContain(secret, masked);
        Assert.Contains(token, masked);
    }

    [Fact]
    public void AMessageWithSeveralKindsOfPhiIsFullyMasked()
    {
        const string input =
            "Validation failed: field memberId is required. Submitted record had Name: Dana Whitfield, " +
            "DOB: 1974-03-02, SSN: 912-55-1173, contact d.whitfield42@example.org / 602-555-0134.";

        var masked = PhiMasker.Mask(input);

        Assert.NotNull(masked);
        Assert.DoesNotContain("Dana Whitfield", masked);
        Assert.DoesNotContain("1974-03-02", masked);
        Assert.DoesNotContain("912-55-1173", masked);
        Assert.DoesNotContain("d.whitfield42@example.org", masked);
        Assert.DoesNotContain("602-555-0134", masked);

        // The shape of the message survives, so it is still diagnosable.
        Assert.Contains("field memberId is required", masked);
    }

    [Fact]
    public void StackTracesAreMaskedToo()
    {
        const string stack = """
            at Downstream.Client.SubmitAsync(Batch batch) in /src/Client.cs:line 148
            --- context ---
               member={ MemberId: MBR-4471902, Name: Dana Whitfield, DOB: 1974-03-02, SSN: 912-55-1173 }
            """;

        var masked = PhiMasker.Mask(stack);

        Assert.NotNull(masked);
        Assert.DoesNotContain("MBR-4471902", masked);
        Assert.DoesNotContain("Dana Whitfield", masked);
        Assert.DoesNotContain("912-55-1173", masked);

        // File and line survive: that is the part that is actually useful.
        Assert.Contains("/src/Client.cs:line 148", masked);
    }

    [Fact]
    public void IsoTimestampsInLogLinesAreNotMistakenForDatesOfBirth()
    {
        const string input = "Timeout at 2026-03-14T02:41:09Z after 30000ms";
        Assert.Equal(input, PhiMasker.Mask(input));
    }

    [Fact]
    public void LongIdentifiersAreNotMistakenForPhoneNumbers()
    {
        const string input = "batch CLM-2026-00142 with 8005551234567 records";
        var masked = PhiMasker.Mask(input);

        Assert.NotNull(masked);
        Assert.DoesNotContain(PhiMasker.PhoneToken, masked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullAndBlankPassThrough(string? input)
        => Assert.Equal(input, PhiMasker.Mask(input));

    [Fact]
    public void CleanTextIsUnchanged()
    {
        const string input = "HTTP 503 Service Unavailable from ach-gateway/submit.";
        Assert.Equal(input, PhiMasker.Mask(input));
        Assert.False(PhiMasker.ContainsPhi(input));
    }

    [Fact]
    public void ContainsPhiDetectsSomethingWorthMasking()
        => Assert.True(PhiMasker.ContainsPhi("Name: Dana Whitfield"));

    [Fact]
    public void MaskingIsIdempotent()
    {
        const string input = "Name: Dana Whitfield, DOB: 1974-03-02, SSN: 912-55-1173";

        var once = PhiMasker.Mask(input);
        var twice = PhiMasker.Mask(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void EverySyntheticFailureMessageIsMaskedBeforePersistence()
    {
        var payload = new JobPayload
        {
            BatchId = "ENC-2026-00042",
            MemberCount = 12,
            Member = new SyntheticMember
            {
                Id = "MBR-4471902",
                Name = "Dana Whitfield",
                Dob = "1974-03-02",
                Phone = "602-555-0134",
                Email = "d.whitfield42@example.org",
                Ssn = "912-55-1173",
                ProviderId = "PRV-884120"
            }
        };

        foreach (var explanation in CauseRules.All)
        {
            var (message, stack) = SyntheticErrorText.Compose(
                explanation.Signature, payload, "state-a-mmis");

            foreach (var text in new[] { PhiMasker.Mask(message), PhiMasker.Mask(stack) })
            {
                Assert.NotNull(text);
                Assert.DoesNotContain("Dana Whitfield", text);
                Assert.DoesNotContain("MBR-4471902", text);
                Assert.DoesNotContain("912-55-1173", text);
                Assert.DoesNotContain("d.whitfield42@example.org", text);
                Assert.DoesNotContain("602-555-0134", text);
            }
        }
    }
}
