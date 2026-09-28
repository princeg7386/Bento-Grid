using CycleGuard.Api.Api;
using CycleGuard.Api.Configuration;
using CycleGuard.Api.Data;
using CycleGuard.Api.Demo;
using CycleGuard.Api.Domain;
using CycleGuard.Api.Downstream;
using CycleGuard.Api.Queue;
using CycleGuard.Api.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CycleGuard.Tests;

/// <summary>
/// A real service graph over a real SQLite file. Tests deliberately do not use an in-memory
/// provider: the behaviour under test is SQLite's, including WAL, busy_timeout and
/// UPDATE ... RETURNING.
/// </summary>
internal sealed class QueueHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private QueueHarness(ServiceProvider provider, string databasePath, FakeTimeProvider? fakeTime)
    {
        _provider = provider;
        DatabasePath = databasePath;
        FakeTime = fakeTime;
    }

    public string DatabasePath { get; }

    /// <summary>Null when the harness was built on the system clock.</summary>
    public FakeTimeProvider? FakeTime { get; }

    public TimeProvider Time => _provider.GetRequiredService<TimeProvider>();

    public JobQueue Queue => _provider.GetRequiredService<JobQueue>();

    public JobExecutor Executor => _provider.GetRequiredService<JobExecutor>();

    public JobReadService Reads => _provider.GetRequiredService<JobReadService>();

    public ScenarioSimulator Scenario => _provider.GetRequiredService<ScenarioSimulator>();

    public OutageRegistry Outages => _provider.GetRequiredService<OutageRegistry>();

    public RawErrorVault RawErrors => _provider.GetRequiredService<RawErrorVault>();

    public SimulationState Simulation => _provider.GetRequiredService<SimulationState>();

    public CycleGuardOptions Options => _provider.GetRequiredService<IOptions<CycleGuardOptions>>().Value;

    public IDbContextFactory<CycleGuardDbContext> Factory
        => _provider.GetRequiredService<IDbContextFactory<CycleGuardDbContext>>();

    public DateTime UtcNow => Time.UtcNow();

    /// <summary>
    /// Build a harness. By default the clock is a FakeTimeProvider pinned to a fixed instant
    /// and time compression is off (scale 1), so backoff arithmetic in tests is literal.
    /// </summary>
    public static async Task<QueueHarness> CreateAsync(
        Action<CycleGuardOptions>? configure = null,
        bool useSystemClock = false)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"cycleguard-test-{Guid.NewGuid():N}.db");

        var options = new CycleGuardOptions
        {
            DatabasePath = databasePath,
            Workers = new WorkerOptions { Count = 4, LeaseSeconds = 30, PollIntervalMs = 10, LeaseReaperIntervalMs = 50 },
            Retry = new RetryOptions
            {
                BaseSeconds = 30,
                CapSeconds = 900,
                MaxRetries = 3,
                JitterEnabled = false,
                JitterFraction = 0
            },
            Risk = new RiskOptions { AtRiskThresholdMinutes = 45 },
            Demo = new DemoOptions
            {
                Seed = 20260115,
                TimeScale = 1,
                JobCount = 300,
                CycleCloseHours = 4,
                ExposeRawErrors = true,
                SimulatedLatencyMs = 0
            }
        };

        configure?.Invoke(options);

        FakeTimeProvider? fakeTime = null;
        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

        if (useSystemClock)
        {
            services.AddSingleton(TimeProvider.System);
        }
        else
        {
            fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 3, 14, 2, 0, 0, TimeSpan.Zero));
            services.AddSingleton<TimeProvider>(fakeTime);
        }

        services.AddSingleton(new SimulationState { TimeScale = options.Demo.TimeScale });
        services.AddDbContextFactory<CycleGuardDbContext>(dbOptions =>
        {
            dbOptions.UseSqlite($"Data Source={databasePath}");
            dbOptions.AddInterceptors(new SqlitePragmaInterceptor());
        });

        services.AddSingleton<OutageRegistry>();
        services.AddSingleton<RawErrorVault>();
        services.AddSingleton<IDownstreamGateway, DownstreamSimulator>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<JobExecutor>();
        services.AddSingleton<JobReadService>();
        services.AddSingleton<ScenarioSimulator>();

        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IDbContextFactory<CycleGuardDbContext>>();
        await using var dbContext = await factory.CreateDbContextAsync();
        await DatabaseBootstrapper.InitialiseAsync(dbContext);

        return new QueueHarness(provider, databasePath, fakeTime);
    }

    public void Advance(TimeSpan amount) => FakeTime?.Advance(amount);

    /// <summary>Enqueue a job with sensible test defaults.</summary>
    public async Task<Job> EnqueueAsync(
        JobType type = JobType.ClaimsBatchAdjudication,
        TimeSpan? deadlineIn = null,
        long amountCents = 100_000,
        FailureScript? script = null,
        string? idempotencyKey = null,
        string? downstreamKey = null,
        string? endpoint = null,
        int? maxAttempts = null)
    {
        var payload = new JobPayload
        {
            BatchId = "TEST-0001",
            MemberCount = 3,
            Member = new SyntheticMember
            {
                Id = "MBR-4471902",
                Name = "Dana Whitfield",
                Dob = "1974-03-02",
                Phone = "602-555-0134",
                Email = "d.whitfield42@example.org",
                Ssn = "912-55-1173",
                ProviderId = "PRV-884120"
            },
            Script = script
        };

        var result = await Queue.EnqueueAsync(new EnqueueRequest(
            type,
            JobPayloadCodec.Serialise(payload),
            UtcNow.Add(deadlineIn ?? TimeSpan.FromHours(4)),
            amountCents,
            idempotencyKey,
            endpoint,
            downstreamKey,
            IsSynthetic: true,
            maxAttempts));

        Assert.Null(result.Error);
        Assert.NotNull(result.Job);
        return result.Job!;
    }

    /// <summary>Claim and run one job end to end, the way a worker would.</summary>
    public async Task<Job?> RunOneAsync(string workerId = "worker-1")
    {
        var job = await Queue.TryClaimAsync(workerId);
        if (job is null)
        {
            return null;
        }

        await Executor.ExecuteAsync(job, workerId, CancellationToken.None);
        return job;
    }

    public async Task<Job> ReloadAsync(long jobId)
    {
        await using var dbContext = await Factory.CreateDbContextAsync();
        return await dbContext.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
    }

    public async Task<List<JobAttempt>> AttemptsAsync(long jobId)
    {
        await using var dbContext = await Factory.CreateDbContextAsync();
        return await dbContext.JobAttempts.AsNoTracking()
            .Where(a => a.JobId == jobId)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync();
    }

    public async Task<int> LedgerCountAsync(string downstreamKey)
    {
        await using var dbContext = await Factory.CreateDbContextAsync();
        return await dbContext.DownstreamLedger.CountAsync(e => e.IdempotencyKey == downstreamKey);
    }

    public async Task<List<AuditEvent>> EventsAsync(long? jobId = null)
    {
        await using var dbContext = await Factory.CreateDbContextAsync();
        var query = dbContext.AuditEvents.AsNoTracking();
        if (jobId is not null)
        {
            query = query.Where(e => e.JobId == jobId);
        }

        return await query.OrderBy(e => e.Id).ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();

        // SQLite keeps pooled handles; clearing them lets the temp files actually delete.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = DatabasePath + suffix;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a test over.
            }
        }
    }
}
