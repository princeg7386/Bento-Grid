using System.Text.RegularExpressions;

namespace CycleGuard.Api.Domain;

/// <summary>
/// Masks member identifiers, dates of birth, SSN-shaped numbers, phone numbers, email
/// addresses and Name: fields out of exception messages and stack traces before anything
/// is written to the database or returned over the API.
///
/// This is defense in depth against accidental PHI capture in error text. It is not a
/// compliance certification and it is not a substitute for not putting PHI in exceptions.
/// Ordering matters: the most specific shapes run first so a looser pattern cannot eat
/// a value a tighter one would have labelled correctly.
/// </summary>
public static partial class PhiMasker
{
    public const string EmailToken = "[EMAIL-REDACTED]";
    public const string SsnToken = "[SSN-REDACTED]";
    public const string MemberToken = "[MEMBER-ID-REDACTED]";
    public const string DobToken = "[DOB-REDACTED]";
    public const string DateToken = "[DATE-REDACTED]";
    public const string NameToken = "[NAME-REDACTED]";
    public const string PhoneToken = "[PHONE-REDACTED]";

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        // 1. Email first: it contains characters the later rules would otherwise chop up.
        (EmailRegex(), EmailToken),

        // 2. SSN-shaped, both labelled and bare dashed form, before any phone rule.
        (LabelledSsnRegex(), $"$1: {SsnToken}"),
        (BareSsnRegex(), SsnToken),

        // 3. Member identifiers, labelled then the bare synthetic formats.
        (LabelledMemberRegex(), $"$1: {MemberToken}"),
        (BareMemberRegex(), MemberToken),

        // 4. Dates of birth. Only masked when labelled, so ISO timestamps in log lines survive.
        (LabelledDobRegex(), $"$1: {DobToken}"),

        // 5. Any US-format date. These are never timestamps in this codebase.
        (UsDateRegex(), DateToken),

        // 6. Name: fields.
        (NameFieldRegex(), $"$1: {NameToken}"),

        // 7. Phone numbers last, and only with a separator present, so plain long
        //    identifiers (claim ids, batch ids) are not mistaken for phone numbers.
        (PhoneRegex(), PhoneToken)
    ];

    /// <summary>Mask a message or stack trace. Null and whitespace pass through untouched.</summary>
    public static string? Mask(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var masked = input;
        foreach (var (pattern, replacement) in Rules)
        {
            masked = pattern.Replace(masked, replacement);
        }

        return masked;
    }

    /// <summary>True when masking would change the text, i.e. the input carried something PHI-shaped.</summary>
    public static bool ContainsPhi(string? input) => Mask(input) != input;

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(SSN|SocialSecurityNumber|social_security_number)\b\s*[:=#]?\s*\d{3}-?\d{2}-?\d{4}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LabelledSsnRegex();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex BareSsnRegex();

    [GeneratedRegex(@"\b(MemberId|Member[ _]?ID|member_id|MBR|MID|MEM)\b\s*[:=#\-]?\s*[A-Za-z0-9][A-Za-z0-9\-]{4,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LabelledMemberRegex();

    [GeneratedRegex(@"\b(?:MBR-\d{5,}|M\d{8,12})\b", RegexOptions.Compiled)]
    private static partial Regex BareMemberRegex();

    [GeneratedRegex(@"\b(DOB|DateOfBirth|date_of_birth|BirthDate|birth_date)\b\s*[:=]?\s*\d{1,4}[/\-.]\d{1,2}[/\-.]\d{2,4}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LabelledDobRegex();

    [GeneratedRegex(@"\b\d{1,2}/\d{1,2}/\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex UsDateRegex();

    [GeneratedRegex(@"\b(Name|PatientName|patient_name|SubscriberName)\b\s*[:=]\s*(?:""[^""\n]*""|[^,;)\]}\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex NameFieldRegex();

    [GeneratedRegex(@"(?:\+?1[-. ])?(?:\(\d{3}\)\s?|\b\d{3}[-. ])\d{3}[-. ]\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();
}
