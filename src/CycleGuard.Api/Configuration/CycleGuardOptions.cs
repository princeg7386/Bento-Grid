using CycleGuard.Api.Domain;

namespace CycleGuard.Api.Configuration;

public sealed class CycleGuardOptions
{
    public const string SectionName = "CycleGuard";

    public string DatabasePath { get; set; } = "cycleguard.db";

    public WorkerOptions Workers { get; set; } = new();

    public RetryOptions Retry { get; set; } = new();

    public RiskOptions Risk { get; set; } = new();

    public DemoOptions Demo { get; set; } = new();
}

public sealed class WorkerOptions
{
    /// <summary>Number of concurrent queue-draining workers.</summary>
    public int Count { get; set; } = 4;

    /// <summary>How long a claim is valid before another worker may reclaim the job.</summary>
    public int LeaseSeconds { get; set; } = 30;

    /// <summary>Idle poll interval when the queue has nothing ready.</summary>
    public int PollIntervalMs { get; set; } = 200;

    public int LeaseReaperIntervalMs { get; set; } = 1000;
}

public sealed class RetryOptions
{
    public int BaseSeconds { get; set; } = 30;

    public int CapSeconds { get; set; } = 900;

    /// <summary>Retries after the first attempt. Total attempts allowed is this plus one.</summary>
    public int MaxRetries { get; set; } = 5;

    public bool JitterEnabled { get; set; } = true;

    public double JitterFraction { get; set; } = 0.2;

    /// <summary>Per-job-type overrides, keyed by <see cref="JobType"/> name.</summary>
    public Dictionary<string, RetryOverride> PerJobType { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve the effective policy for a job type: per-type override wins over global.</summary>
    public BackoffPolicy PolicyFor(JobType type)
    {
        PerJobType.TryGetValue(type.ToString(), out var over);
        return new BackoffPolicy(
            over?.BaseSeconds ?? BaseSeconds,
            over?.CapSeconds ?? CapSeconds,
            over?.MaxRetries ?? MaxRetries,
            JitterEnabled,
            JitterFraction);
    }

    /// <summary>Total attempts allowed for a job type, including the first one.</summary>
    public int MaxAttemptsFor(JobType type) => PolicyFor(type).MaxRetries + 1;
}

public sealed class RetryOverride
{
    public int? MaxRetries { get; set; }

    public int? BaseSeconds { get; set; }

    public int? CapSeconds { get; set; }
}

public sealed class RiskOptions
{
    /// <summary>
    /// Slack below which a job is AtRisk, expressed in the simulated minutes the analyst
    /// reads on the dashboard.
    /// </summary>
    public double AtRiskThresholdMinutes { get; set; } = 45;
}

public sealed class DemoOptions
{
    /// <summary>Fixed seed so "Simulate last night" is identical every run.</summary>
    public int Seed { get; set; } = 20260115;

    /// <summary>Simulated seconds per real second. 60 turns minutes of backoff into seconds.</summary>
    public double TimeScale { get; set; } = 60;

    public int JobCount { get; set; } = 300;

    public double CycleCloseHours { get; set; } = 4;

    /// <summary>
    /// When true the API may return the unmasked synthetic error text held in memory, so the
    /// demo can show raw versus masked. Raw text is never persisted either way.
    /// </summary>
    public bool ExposeRawErrors { get; set; } = true;

    /// <summary>
    /// Fake network latency per downstream call, so the dashboard has visible motion.
    /// Set to 0 in tests: a real delay on a fake clock would otherwise never elapse.
    /// </summary>
    public int SimulatedLatencyMs { get; set; } = 15;
}
