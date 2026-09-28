namespace CycleGuard.Api.Configuration;

/// <summary>
/// The parts of the simulation an operator can change at runtime from the dashboard.
/// Time scale is read on every backoff calculation, so it must be safe to write from
/// an API thread while workers read it.
/// </summary>
public sealed class SimulationState
{
    private double _timeScale = 60;
    private long _cycleCloseTicks;
    private long _scenarioSeededTicks;

    public double TimeScale
    {
        get => Volatile.Read(ref _timeScale);
        set => Volatile.Write(ref _timeScale, Math.Clamp(value, 1, 3600));
    }

    /// <summary>When the payment cycle closes. Zero means no scenario has been seeded.</summary>
    public long CycleCloseTicks
    {
        get => Interlocked.Read(ref _cycleCloseTicks);
        set => Interlocked.Exchange(ref _cycleCloseTicks, value);
    }

    public long ScenarioSeededTicks
    {
        get => Interlocked.Read(ref _scenarioSeededTicks);
        set => Interlocked.Exchange(ref _scenarioSeededTicks, value);
    }

    public bool HasScenario => CycleCloseTicks > 0;

    /// <summary>Compress a simulated duration into the real time the demo actually waits.</summary>
    public double ToRealSeconds(double simulatedSeconds) => simulatedSeconds / Math.Max(1.0, TimeScale);

    /// <summary>Expand a real duration into the simulated time the analyst sees.</summary>
    public double ToSimulatedSeconds(double realSeconds) => realSeconds * Math.Max(1.0, TimeScale);

    public void Reset()
    {
        CycleCloseTicks = 0;
        ScenarioSeededTicks = 0;
    }
}
