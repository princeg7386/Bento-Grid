using CycleGuard.Api.Configuration;
using CycleGuard.Api.Demo;
using CycleGuard.Api.Domain;
using CycleGuard.Api.Queue;
using Microsoft.Extensions.Options;

namespace CycleGuard.Api.Api;

public static class EndpointRoutes
{
    public static void MapCycleGuardEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").WithTags("CycleGuard");

        api.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .WithSummary("Liveness probe.");

        // ------------------------------------------------------------------ jobs

        api.MapPost("/jobs", async (CreateJobRequest request, JobQueue queue, JobReadService reads, CancellationToken ct) =>
            {
                if (!Enum.TryParse<JobType>(request.Type, true, out var type))
                {
                    return Results.BadRequest(new
                    {
                        error = $"Unknown job type '{request.Type}'.",
                        allowed = Enum.GetNames<JobType>()
                    });
                }

                if (request.DeadlineUtc == default)
                {
                    return Results.BadRequest(new { error = "DeadlineUtc is required." });
                }

                var result = await queue.EnqueueAsync(
                    new EnqueueRequest(
                        type,
                        request.Payload ?? "{}",
                        DateTime.SpecifyKind(request.DeadlineUtc, DateTimeKind.Utc),
                        request.AmountAtStakeCents,
                        request.IdempotencyKey,
                        request.DownstreamEndpoint,
                        request.DownstreamIdempotencyKey,
                        IsSynthetic: false,
                        request.MaxAttempts),
                    ct);

                if (result.DuplicateKey)
                {
                    return Results.Conflict(new { error = result.Error });
                }

                if (result.Job is null)
                {
                    return Results.BadRequest(new { error = result.Error });
                }

                return Results.Created($"/api/jobs/{result.Job.Id}", reads.ToSummary(result.Job));
            })
            .WithSummary("Enqueue a job.")
            .WithDescription("Idempotency keys are unique: re-posting the same key returns 409 rather than creating a second job.");

        api.MapGet("/jobs", async (
                string? state,
                string? risk,
                string? endpoint,
                int? limit,
                JobReadService reads,
                CancellationToken ct) =>
            {
                var jobs = await reads.ListJobsAsync(state, risk, endpoint, limit, ct);
                return Results.Ok(limit is > 0 ? jobs.Take(limit.Value).ToList() : jobs);
            })
            .WithSummary("List jobs, sorted by time to breach.")
            .WithDescription("Filter by state, risk and downstream endpoint. Default order is soonest-to-cost-money first.");

        api.MapGet("/jobs/{id:long}", async (long id, JobReadService reads, CancellationToken ct) =>
            {
                var job = await reads.GetJobAsync(id, ct);
                return job is null ? Results.NotFound(new { error = $"No job {id}." }) : Results.Ok(job);
            })
            .WithSummary("Job detail: attempts, backoff schedule, masked errors and audit events.");

        api.MapPost("/jobs/{id:long}/requeue", async (
                long id,
                RequeueRequest request,
                JobQueue queue,
                JobReadService reads,
                CancellationToken ct) =>
            {
                var result = await queue.RequeueAsync(id, request.Analyst, request.Note, ct);

                return result.Outcome switch
                {
                    RequeueOutcome.Requeued => Results.Ok(new
                    {
                        requeued = true,
                        job = result.Job is null ? null : reads.ToSummary(result.Job)
                    }),
                    RequeueOutcome.MissingAnalystDetails => Results.BadRequest(new
                    {
                        error = "Both 'analyst' and 'note' are required. A requeue is an auditable action."
                    }),
                    RequeueOutcome.NotFound => Results.NotFound(new { error = $"No job {id}." }),
                    _ => Results.Conflict(new
                    {
                        error = "Job is not dead-lettered, so it cannot be requeued.",
                        state = result.Job?.State.ToString(),
                        note = "If several requeues raced, exactly one won and this was not it."
                    })
                };
            })
            .WithSummary("Requeue a dead-lettered job.")
            .WithDescription("Requires an analyst name and a note. Keeps the attempt history and reuses the idempotency key, so the downstream cannot be double-applied.");

        api.MapGet("/deadletters", async (JobReadService reads, CancellationToken ct) =>
                Results.Ok(await reads.ListDeadLettersAsync(ct)))
            .WithSummary("Dead-lettered jobs, grouped by cause.");

        api.MapGet("/status", async (JobReadService reads, CancellationToken ct) =>
                Results.Ok(await reads.GetStatusAsync(ct)))
            .WithSummary("Counts, dollars at risk, cycle countdown, throughput and retry rate.");

        api.MapGet("/groups", async (JobReadService reads, CancellationToken ct) =>
                Results.Ok(await reads.GetGroupsAsync(ct)))
            .WithSummary("Root-cause groups by (endpoint, failure signature).");

        api.MapGet("/state-machine", () => Results.Ok(
                JobStateMachine.Table.ToDictionary(
                    pair => pair.Key.ToString(),
                    pair => pair.Value.Select(state => state.ToString()).ToArray())))
            .WithSummary("The legal state transition table.");

        api.MapGet("/causes", () => Results.Ok(CauseRules.All))
            .WithSummary("The deterministic failure-signature to plain-English rule table.");

        // ------------------------------------------------------------------ demo

        var demo = api.MapGroup("/demo").WithTags("Demo");

        demo.MapPost("/scenarios/last-night", async (ScenarioSimulator simulator, CancellationToken ct) =>
                Results.Ok(await simulator.SeedLastNightAsync(ct)))
            .WithSummary("Seed the deterministic overnight scenario (about 300 synthetic jobs).");

        demo.MapPost("/reset", async (ScenarioSimulator simulator, CancellationToken ct) =>
            {
                await simulator.ResetAsync(ct);
                return Results.Ok(new { reset = true });
            })
            .WithSummary("Clear all jobs, attempts, ledger entries and events.");

        demo.MapPost("/timescale", (TimeScaleRequest request, SimulationState simulation) =>
            {
                if (request.TimeScale is < 1 or > 3600)
                {
                    return Results.BadRequest(new { error = "TimeScale must be between 1 and 3600." });
                }

                simulation.TimeScale = request.TimeScale;
                return Results.Ok(new { timeScale = simulation.TimeScale });
            })
            .WithSummary("Change how aggressively simulated time is compressed.")
            .WithDescription("Affects future backoff waits only. Deadlines already written keep the scale they were created with.");

        demo.MapPost("/kill-worker", async (ScenarioSimulator simulator, CancellationToken ct) =>
            {
                var result = await simulator.KillWorkerMidPaymentAsync(ct);
                return result.Killed
                    ? Results.Ok(result)
                    : Results.Conflict(new { error = result.Reason });
            })
            .WithSummary("Expire the lease on a running payment job, as if its worker had died.");

        demo.MapGet("/config", (IOptions<CycleGuardOptions> options, SimulationState simulation) => Results.Ok(new
            {
                seed = options.Value.Demo.Seed,
                timeScale = simulation.TimeScale,
                jobCount = options.Value.Demo.JobCount,
                cycleCloseHours = options.Value.Demo.CycleCloseHours,
                workers = options.Value.Workers.Count,
                atRiskThresholdMinutes = options.Value.Risk.AtRiskThresholdMinutes,
                exposeRawErrors = options.Value.Demo.ExposeRawErrors
            }))
            .WithSummary("The knobs the dashboard shows.");
    }
}
