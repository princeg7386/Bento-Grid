using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CycleGuard.Api.Domain;
using CycleGuard.Api.Downstream;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace CycleGuard.Tests;

/// <summary>
/// Drives the real HTTP API against the real worker pool on a real SQLite file. Backoff is
/// compressed hard (base 2 simulated seconds at scale 100) so the whole retry story plays out
/// in well under a second of wall clock.
/// </summary>
public sealed class CycleGuardApp : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"cycleguard-api-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("CycleGuard:DatabasePath", _databasePath);
        builder.UseSetting("CycleGuard:Workers:Count", "4");
        builder.UseSetting("CycleGuard:Workers:PollIntervalMs", "20");
        builder.UseSetting("CycleGuard:Workers:LeaseReaperIntervalMs", "50");
        builder.UseSetting("CycleGuard:Retry:BaseSeconds", "2");
        builder.UseSetting("CycleGuard:Retry:CapSeconds", "10");
        builder.UseSetting("CycleGuard:Retry:MaxRetries", "3");
        builder.UseSetting("CycleGuard:Retry:JitterEnabled", "false");

        // appsettings.json gives PaymentRunDisbursement its own base/cap, and a per-job-type
        // override beats the global one. Without these two lines the test asserts against the
        // global curve while the engine uses the payment curve.
        builder.UseSetting("CycleGuard:Retry:PerJobType:PaymentRunDisbursement:BaseSeconds", "2");
        builder.UseSetting("CycleGuard:Retry:PerJobType:PaymentRunDisbursement:CapSeconds", "10");
        builder.UseSetting("CycleGuard:Retry:PerJobType:PaymentRunDisbursement:MaxRetries", "3");
        builder.UseSetting("CycleGuard:Demo:TimeScale", "100");
        builder.UseSetting("CycleGuard:Demo:SimulatedLatencyMs", "0");
        builder.UseSetting("CycleGuard:Demo:JobCount", "300");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(_databasePath + suffix))
                {
                    File.Delete(_databasePath + suffix);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}

public class ApiIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AJobThatFailsTwiceThenSucceedsShowsTheExpectedAttemptSequence()
    {
        await using var app = new CycleGuardApp();
        var client = app.CreateClient();

        var payload = JobPayloadCodec.Serialise(new JobPayload
        {
            BatchId = "PAY-2026-00042",
            MemberCount = 4,
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
            Script = new FailureScript { Signature = FailureSignatures.Http503, FailUntilAttempt = 2 }
        });

        var create = await client.PostAsJsonAsync("/api/jobs", new
        {
            type = nameof(JobType.PaymentRunDisbursement),
            payload,
            deadlineUtc = DateTime.UtcNow.AddHours(4),
            amountAtStakeCents = 4_412_900L,
            idempotencyKey = "api-fail-twice-then-succeed"
        });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = created.GetProperty("id").GetInt64();

        var detail = await WaitForStateAsync(client, jobId, nameof(JobState.Succeeded));

        Assert.Equal(3, detail.GetProperty("summary").GetProperty("attempts").GetInt32());

        var attempts = detail.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(3, attempts.Length);
        Assert.Equal(nameof(AttemptOutcome.TransientFailure), attempts[0].GetProperty("outcome").GetString());
        Assert.Equal(nameof(AttemptOutcome.TransientFailure), attempts[1].GetProperty("outcome").GetString());
        Assert.Equal(nameof(AttemptOutcome.Succeeded), attempts[2].GetProperty("outcome").GetString());

        // Backoff doubled, in simulated seconds, exactly as the policy says.
        Assert.Equal(2, attempts[0].GetProperty("backoffSeconds").GetInt32());
        Assert.Equal(4, attempts[1].GetProperty("backoffSeconds").GetInt32());

        // What came back over the wire is masked, and the raw form is available separately.
        var masked = attempts[0].GetProperty("errorMasked").GetString();
        Assert.NotNull(masked);
        Assert.DoesNotContain("MBR-4471902", masked);
        Assert.DoesNotContain("602-555-0134", masked);

        var raw = attempts[0].GetProperty("errorRaw").GetString();
        Assert.NotNull(raw);
        Assert.Contains("MBR-4471902", raw);
    }

    [Fact]
    public async Task DuplicateIdempotencyKeysAreRejectedWithConflict()
    {
        await using var app = new CycleGuardApp();
        var client = app.CreateClient();

        var body = new
        {
            type = nameof(JobType.ClaimsBatchAdjudication),
            payload = "{}",
            deadlineUtc = DateTime.UtcNow.AddHours(3),
            amountAtStakeCents = 12_345L,
            idempotencyKey = "api-duplicate-key"
        };

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/jobs", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/jobs", body)).StatusCode);
    }

    [Fact]
    public async Task AnUnknownJobTypeIsRejected()
    {
        await using var app = new CycleGuardApp();
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/jobs", new
        {
            type = "PayEverybodyImmediately",
            deadlineUtc = DateTime.UtcNow.AddHours(1),
            amountAtStakeCents = 1L
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheSeededScenarioProducesARiskSortedListAndDollarsAtRisk()
    {
        await using var app = new CycleGuardApp();
        var client = app.CreateClient();

        var seed = await client.PostAsync("/api/demo/scenarios/last-night", null);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        var scenario = await seed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(300, scenario.GetProperty("jobsCreated").GetInt32());
        Assert.Equal(8, scenario.GetProperty("permanentFailures").GetInt32());
        Assert.Equal(20, scenario.GetProperty("outageStalledJobs").GetInt32());
        Assert.Equal(2, scenario.GetProperty("duplicateSubmissions").GetInt32());

        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.True(status.GetProperty("scenarioLoaded").GetBoolean());
        Assert.Equal(300, status.GetProperty("totalJobs").GetInt32());
        Assert.True(status.GetProperty("dollarsAtRiskCents").GetInt64() > 0);
        Assert.True(status.GetProperty("secondsToCycleClose").GetInt64() > 0);

        var jobs = await client.GetFromJsonAsync<JsonElement>("/api/jobs?limit=50");
        var deadlines = jobs.EnumerateArray()
            .Select(job => job.GetProperty("secondsToDeadline").GetInt64())
            .ToArray();

        Assert.NotEmpty(deadlines);

        // The default order is soonest-to-cost-money first.
        for (var index = 1; index < deadlines.Length; index++)
        {
            Assert.True(
                deadlines[index] >= deadlines[index - 1],
                $"Job list is not sorted by time to breach: {deadlines[index - 1]} then {deadlines[index]}.");
        }
    }

    [Fact]
    public async Task DeadLettersGroupByCauseAndCarryPlainEnglish()
    {
        await using var app = new CycleGuardApp();
        var client = app.CreateClient();

        await client.PostAsync("/api/demo/scenarios/last-night", null);

        // The eight permanent failures dead-letter on their first attempt.
        var groups = await WaitForAsync(
            async () => (await client.GetFromJsonAsync<JsonElement>("/api/deadletters")).EnumerateArray().ToArray(),
            array => array.Length > 0,
            "the permanent failures to dead-letter");

        foreach (var group in groups)
        {
            Assert.False(string.IsNullOrWhiteSpace(group.GetProperty("cause").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(group.GetProperty("suggestedAction").GetString()));
        }

        var causes = await client.GetFromJsonAsync<JsonElement>("/api/causes");
        Assert.True(causes.EnumerateArray().Count() > 10);
    }

    [Fact]
    public async Task RequeueOverHttpNeedsAnAnalystAndCanOnlyWinOnce()
    {
        await using var app = new CycleGuardApp();
        var client = app.CreateClient();

        // This job is built rather than borrowed from the seeded scenario on purpose. The
        // seeded dead letters are *permanent* failures: they re-enter the dead-letter table
        // after every requeue, so any assertion about a later requeue being refused is a race.
        // This one exhausts a single attempt and then succeeds, so once it is requeued it
        // leaves the dead-letter table for good and every later requeue is refused.
        var payload = JobPayloadCodec.Serialise(new JobPayload
        {
            BatchId = "PAY-2026-00099",
            MemberCount = 1,
            Member = new SyntheticMember { Id = "MBR-4471902", Name = "Dana Whitfield" },
            Script = new FailureScript { Signature = FailureSignatures.Http503, FailUntilAttempt = 1 },
        });

        var create = await client.PostAsJsonAsync("/api/jobs", new
        {
            type = nameof(JobType.PaymentRunDisbursement),
            payload,
            deadlineUtc = DateTime.UtcNow.AddHours(4),
            amountAtStakeCents = 4_412_900L,
            idempotencyKey = "api-requeue-contract",
            maxAttempts = 1,
        });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var jobId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        await WaitForStateAsync(client, jobId, nameof(JobState.DeadLettered));

        // A requeue is an auditable action, so it refuses to happen anonymously.
        var missingBoth = await client.PostAsJsonAsync($"/api/jobs/{jobId}/requeue", new { analyst = "", note = "" });
        Assert.Equal(HttpStatusCode.BadRequest, missingBoth.StatusCode);

        var missingNote = await client.PostAsJsonAsync($"/api/jobs/{jobId}/requeue", new { analyst = "Priya S", note = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, missingNote.StatusCode);

        // Still dead-lettered: a rejected requeue changes nothing.
        var stillDead = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
        Assert.Equal(nameof(JobState.DeadLettered), stillDead.GetProperty("summary").GetProperty("state").GetString());

        var accepted = await client.PostAsJsonAsync(
            $"/api/jobs/{jobId}/requeue",
            new { analyst = "Priya S", note = "Gateway confirmed the 503 was theirs." });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // From here the job is Queued, Running or Succeeded, and every one of those refuses a
        // requeue, so this is deterministic however the race lands.
        var refused = await client.PostAsJsonAsync(
            $"/api/jobs/{jobId}/requeue",
            new { analyst = "Marcus O", note = "Trying the same thing again." });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("not dead-lettered", body.GetProperty("error").GetString()!);

        // And the requeue it did accept actually worked.
        var final = await WaitForStateAsync(client, jobId, nameof(JobState.Succeeded));
        Assert.Equal(1, final.GetProperty("summary").GetProperty("requeueCount").GetInt32());
        Assert.Equal(2, final.GetProperty("summary").GetProperty("attempts").GetInt32());

        // The first attempt is still on the record: requeue keeps history.
        Assert.Equal(2, final.GetProperty("attempts").EnumerateArray().Count());
    }

    [Fact]
    public async Task GroupsExplainFailuresByEndpointAndSignature()
    {
        await using var app = new CycleGuardApp();
        var client = app.CreateClient();

        await client.PostAsync("/api/demo/scenarios/last-night", null);

        var groups = await WaitForAsync(
            async () => (await client.GetFromJsonAsync<JsonElement>("/api/groups")).EnumerateArray().ToArray(),
            array => array.Length > 0,
            "root-cause groups to form");

        var group = groups[0];
        Assert.False(string.IsNullOrWhiteSpace(group.GetProperty("downstreamEndpoint").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(group.GetProperty("failureSignature").GetString()));
        Assert.True(group.GetProperty("jobCount").GetInt32() > 0);
    }

    [Fact]
    public async Task ResetEmptiesEverything()
    {
        await using var app = new CycleGuardApp();
        var client = app.CreateClient();

        await client.PostAsync("/api/demo/scenarios/last-night", null);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/demo/reset", null)).StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal(0, status.GetProperty("totalJobs").GetInt32());
        Assert.False(status.GetProperty("scenarioLoaded").GetBoolean());
    }

    [Fact]
    public async Task TimeScaleCanBeChangedAndIsValidated()
    {
        await using var app = new CycleGuardApp();
        var client = app.CreateClient();

        var ok = await client.PostAsJsonAsync("/api/demo/timescale", new { timeScale = 120.0 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(120.0, (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("timeScale").GetDouble());

        var bad = await client.PostAsJsonAsync("/api/demo/timescale", new { timeScale = 0.0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task TheStateMachineTableIsPublished()
    {
        await using var app = new CycleGuardApp();
        var client = app.CreateClient();

        var table = await client.GetFromJsonAsync<JsonElement>("/api/state-machine");

        Assert.Empty(table.GetProperty(nameof(JobState.Succeeded)).EnumerateArray());
        Assert.Contains(
            nameof(JobState.Queued),
            table.GetProperty(nameof(JobState.DeadLettered)).EnumerateArray().Select(e => e.GetString()));
    }

    // ------------------------------------------------------------------ helpers

    private static Task<JsonElement> WaitForStateAsync(HttpClient client, long jobId, string state)
        => WaitForAsync(
            () => client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}"),
            detail => detail.GetProperty("summary").GetProperty("state").GetString() == state,
            $"job {jobId} to reach {state}");

    /// <summary>
    /// Poll until the fetched value satisfies the predicate. Fails the test with a readable
    /// message rather than returning something empty for a later assertion to trip over.
    /// </summary>
    private static async Task<T> WaitForAsync<T>(
        Func<Task<T>> fetch,
        Func<T, bool> ready,
        string description,
        int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        T value = default!;

        while (DateTime.UtcNow < deadline)
        {
            value = await fetch();
            if (ready(value))
            {
                return value;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Timed out after {timeoutSeconds}s waiting for {description}.");
        return value;
    }
}
