namespace CycleGuard.Api.Domain;

/// <summary>
/// TimeProvider hands back DateTimeOffset. CycleGuard never stores or compares
/// DateTimeOffset (see docs/DECISIONS.md), so every read of the clock is converted
/// to UTC DateTime or to UTC ticks right here at the boundary.
/// </summary>
public static class Clock
{
    public static DateTime UtcNow(this TimeProvider timeProvider)
        => timeProvider.GetUtcNow().UtcDateTime;

    public static long UtcTicks(this TimeProvider timeProvider)
        => timeProvider.GetUtcNow().UtcDateTime.Ticks;

    public static DateTime ToUtc(long ticks) => new(ticks, DateTimeKind.Utc);

    public static long Ticks(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).Ticks;
}
