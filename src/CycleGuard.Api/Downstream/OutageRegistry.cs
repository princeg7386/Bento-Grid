using System.Collections.Concurrent;

namespace CycleGuard.Api.Downstream;

/// <summary>
/// Which mock endpoints are currently refusing traffic. The simulator uses this to stall a
/// batch of jobs behind one endpoint and then let them heal, which is the "one outage
/// explains twenty of these" story on the dashboard.
/// </summary>
public sealed class OutageRegistry
{
    private readonly ConcurrentDictionary<string, long> _downUntilTicks = new(StringComparer.OrdinalIgnoreCase);

    public void ScheduleOutage(string endpoint, long untilTicks)
        => _downUntilTicks[endpoint] = untilTicks;

    public bool IsDown(string endpoint, long nowTicks)
        => _downUntilTicks.TryGetValue(endpoint, out var until) && nowTicks < until;

    /// <summary>Endpoints still down, with the tick they recover at.</summary>
    public IReadOnlyDictionary<string, long> Active(long nowTicks)
        => _downUntilTicks
            .Where(pair => nowTicks < pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    public void Clear() => _downUntilTicks.Clear();
}
