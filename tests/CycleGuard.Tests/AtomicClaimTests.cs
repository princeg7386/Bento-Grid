using System.Collections.Concurrent;
using CycleGuard.Api.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CycleGuard.Tests;

public class AtomicClaimTests
{
    [Fact]
    public async Task WorkersTakeTheNearestDeadlineNotTheOldestJob()
    {
        await using var harness = await QueueHarness.CreateAsync();

        // Inserted oldest-first, but deadlines run the other way.
        var late = await harness.EnqueueAsync(deadlineIn: TimeSpan.FromHours(8), idempotencyKey: "late");
        var middle = await harness.EnqueueAsync(deadlineIn: TimeSpan.FromHours(4), idempotencyKey: "middle");
        var urgent = await harness.EnqueueAsync(deadlineIn: TimeSpan.FromMinutes(20), idempotencyKey: "urgent");

        var first = await harness.Queue.TryClaimAsync("worker-1");
        var second = await harness.Queue.TryClaimAsync("worker-2");
        var third = await harness.Queue.TryClaimAsync("worker-3");

        Assert.Equal(urgent.Id, first!.Id);
        Assert.Equal(middle.Id, second!.Id);
        Assert.Equal(late.Id, third!.Id);
    }

    [Fact]
    public async Task ClaimingSetsTheLeaseAndIncrementsAttemptsInOneStatement()
    {
        await using var harness = await QueueHarness.CreateAsync();

        var job = await harness.EnqueueAsync();
        var claimed = await harness.Queue.TryClaimAsync("worker-7");

        Assert.NotNull(claimed);
        Assert.Equal(JobState.Running, claimed!.State);
        Assert.Equal(1, claimed.Attempts);
        Assert.Equal("worker-7", claimed.ClaimedBy);
        Assert.Equal(Clock.Ticks(harness.UtcNow.AddSeconds(30)), claimed.LeaseExpiresTicks);
        Assert.Null(claimed.NextAttemptTicks);

        // And the row really changed, not just the returned copy.
        var reloaded = await harness.ReloadAsync(job.Id);
        Assert.Equal(JobState.Running, reloaded.State);
    }

    [Fact]
    public async Task AnEmptyQueueReturnsNull()
    {
        await using var harness = await QueueHarness.CreateAsync();
        Assert.Null(await harness.Queue.TryClaimAsync("worker-1"));
    }

    [Fact]
    public async Task ARunningJobCannotBeClaimedTwice()
    {
        await using var harness = await QueueHarness.CreateAsync();

        await harness.EnqueueAsync();

        Assert.NotNull(await harness.Queue.TryClaimAsync("worker-1"));
        Assert.Null(await harness.Queue.TryClaimAsync("worker-2"));
    }

    [Fact]
    public async Task ThreeHundredJobsAreClaimedExactlyOnceByEightWorkers()
    {
        // Real clock: this test is about SQLite concurrency, not about time.
        await using var harness = await QueueHarness.CreateAsync(useSystemClock: true);

        const int jobCount = 300;
        const int workerCount = 8;

        for (var index = 0; index < jobCount; index++)
        {
            await harness.EnqueueAsync(
                deadlineIn: TimeSpan.FromMinutes(index + 1),
                idempotencyKey: $"concurrency-{index:D4}");
        }

        var claims = new ConcurrentBag<long>();
        var failures = new ConcurrentBag<Exception>();

        var workers = Enumerable.Range(1, workerCount).Select(worker => Task.Run(async () =>
        {
            var workerId = $"worker-{worker}";
            while (true)
            {
                try
                {
                    var job = await harness.Queue.TryClaimAsync(workerId);
                    if (job is null)
                    {
                        return;
                    }

                    claims.Add(job.Id);

                    // Finish the job so it leaves the claimable set.
                    await harness.Queue.MarkSucceededAsync(job, 0, workerId);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                    return;
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.Empty(failures);

        // Every job claimed, and no job claimed twice.
        Assert.Equal(jobCount, claims.Count);
        Assert.Equal(jobCount, claims.Distinct().Count());
    }

    [Fact]
    public async Task EightWorkersHammeringTheDatabaseNeverSeeSqliteBusy()
    {
        // The full write path: attempt rows, audit rows, ledger inserts and state changes,
        // all from eight threads against one file. WAL plus a per-connection busy_timeout is
        // what makes this survive; without them this test fails with SQLITE_BUSY.
        await using var harness = await QueueHarness.CreateAsync(
            options => options.Workers.LeaseSeconds = 120,
            useSystemClock: true);

        const int jobCount = 300;
        const int workerCount = 8;

        for (var index = 0; index < jobCount; index++)
        {
            await harness.EnqueueAsync(
                type: index % 3 == 0 ? JobType.PaymentRunDisbursement : JobType.ClaimsBatchAdjudication,
                deadlineIn: TimeSpan.FromMinutes(index + 1),
                idempotencyKey: $"hammer-{index:D4}");
        }

        var processed = new ConcurrentBag<long>();
        var errors = new ConcurrentBag<Exception>();

        var workers = Enumerable.Range(1, workerCount).Select(worker => Task.Run(async () =>
        {
            var workerId = $"worker-{worker}";
            while (true)
            {
                try
                {
                    var job = await harness.Queue.TryClaimAsync(workerId);
                    if (job is null)
                    {
                        return;
                    }

                    await harness.Executor.ExecuteAsync(job, workerId, CancellationToken.None);
                    processed.Add(job.Id);
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                    return;
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        var busyErrors = errors
            .Select(Flatten)
            .Where(message =>
                message.Contains("SQLITE_BUSY", StringComparison.OrdinalIgnoreCase)
                || message.Contains("database is locked", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(busyErrors);
        Assert.Empty(errors.Select(Flatten));

        Assert.Equal(jobCount, processed.Count);
        Assert.Equal(jobCount, processed.Distinct().Count());

        await using var dbContext = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(jobCount, await dbContext.Jobs.CountAsync(j => j.State == JobState.Succeeded));
    }

    [Fact]
    public async Task WalAndBusyTimeoutAreActuallyApplied()
    {
        await using var harness = await QueueHarness.CreateAsync();
        await using var dbContext = await harness.Factory.CreateDbContextAsync();

        var connection = (SqliteConnection)dbContext.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var journalCommand = connection.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", ((string)(await journalCommand.ExecuteScalarAsync())!).ToLowerInvariant());

        await using var busyCommand = connection.CreateCommand();
        busyCommand.CommandText = "PRAGMA busy_timeout;";
        Assert.Equal(30_000L, Convert.ToInt64(await busyCommand.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task AJobDeletedAfterClaimIsAbandonedRatherThanCrashingTheWorker()
    {
        // Simulates a demo "reset" racing a worker that already claimed the job: the Jobs
        // row is gone by the time the worker tries to record its attempt. See docs/WHAT_BROKE.md.
        await using var harness = await QueueHarness.CreateAsync();

        var job = await harness.EnqueueAsync();
        var claimed = await harness.Queue.TryClaimAsync("worker-1");
        Assert.NotNull(claimed);

        await using (var dbContext = await harness.Factory.CreateDbContextAsync())
        {
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM Jobs WHERE Id = {0};", claimed!.Id);
        }

        var attempt = await harness.Queue.RecordAttemptStartAsync(claimed!, "worker-1");
        Assert.Null(attempt);

        // The executor must not throw either -- it just abandons the vanished job.
        await harness.Executor.ExecuteAsync(claimed!, "worker-1", CancellationToken.None);
    }

    private static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" <- ", parts);
    }
}
