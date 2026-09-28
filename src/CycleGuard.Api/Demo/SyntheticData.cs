namespace CycleGuard.Api.Demo;

/// <summary>
/// Invented names, identifiers and contact details used to populate synthetic failure text.
/// Nothing here comes from a real person, provider or payer. The values are shaped like PHI
/// specifically so the masking layer has something realistic to strip.
/// </summary>
internal static class SyntheticData
{
    public static readonly string[] FirstNames =
    [
        "Dana", "Marcus", "Priya", "Elena", "Tobias", "Neha", "Caleb", "Rosalind",
        "Omar", "Ingrid", "Devon", "Yusuf", "Marisol", "Theo", "Adaeze", "Lena"
    ];

    public static readonly string[] LastNames =
    [
        "Whitfield", "Okonjo", "Ramanathan", "Castellanos", "Nyberg", "Bhatt", "Ferraro",
        "Ashworth", "Haddad", "Lindqvist", "Mercer", "Osei", "Delgado", "Brennan", "Iwu", "Kovacs"
    ];

    public static readonly string[] StateCodes = ["AZ", "NM", "OR", "TN", "MI", "KY"];

    public static string MemberId(Random random) => $"MBR-{random.Next(1_000_000, 9_999_999)}";

    public static string ProviderId(Random random) => $"PRV-{random.Next(100_000, 999_999)}";

    public static string Dob(Random random)
        => $"{random.Next(1945, 2008)}-{random.Next(1, 13):D2}-{random.Next(1, 29):D2}";

    /// <summary>555-prefixed exchange, reserved for fiction.</summary>
    public static string Phone(Random random)
        => $"{random.Next(200, 989)}-555-{random.Next(100, 999):D4}";

    /// <summary>Uses example.org, which exists for exactly this purpose.</summary>
    public static string Email(string first, string last, Random random)
        => $"{char.ToLowerInvariant(first[0])}.{last.ToLowerInvariant()}{random.Next(10, 99)}@example.org";

    /// <summary>
    /// SSN-shaped but drawn only from the 900 area, which the SSA has never issued.
    /// It has to look like an SSN for the masking demo; it must never be a usable one.
    /// </summary>
    public static string SsnShaped(Random random)
        => $"9{random.Next(10, 99)}-{random.Next(10, 99)}-{random.Next(1000, 9999)}";

    public static string BatchId(string prefix, int index) => $"{prefix}-2026-{index:D5}";
}
