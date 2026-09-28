using CycleGuard.Api.Configuration;
using CycleGuard.Api.Domain;
using CycleGuard.Api.Queue;
using Microsoft.Extensions.Options;

namespace CycleGuard.Api.Downstream;

/// <summary>The mock world outside CycleGuard: claims engine, two state systems, one ACH gateway.</summary>
public interface IDownstreamGateway
{
    Task<ExecutionOutcome> ExecuteAsync(Job job, CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides whether an attempt succeeds or fails, and with what signature. It deliberately
/// does not write anything: persisting the effect is the executor's job, inside the same
/// transaction as the job state change.
/// </summary>
public sealed class DownstreamSimulator(
    OutageRegistry outages,
    TimeProvider timeProvider,
    IOptions<CycleGuardOptions> options,
    ILogger<DownstreamSimulator> logger) : IDownstreamGateway
{
    public async Task<ExecutionOutcome> ExecuteAsync(Job job, CancellationToken cancellationToken = default)
    {
        var payload = JobPayloadCodec.Parse(job.Payload);

        // A little simulated network latency so the dashboard has something to show. Zero in
        // tests, because a real wait on a fake clock would never elapse.
        var latencyMs = options.Value.Demo.SimulatedLatencyMs;
        if (latencyMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(latencyMs), timeProvider, cancellationToken);
        }

        // 1. A hard endpoint outage beats anything the job itself was scripted to do.
        if (outages.IsDown(job.DownstreamEndpoint, timeProvider.UtcTicks()))
        {
            return Fail(FailureSignatures.EndpointOutage, payload, job);
        }

        var script = payload.Script;
        if (script is null || string.IsNullOrWhiteSpace(script.Signature))
        {
            return ExecutionOutcome.Succeeded();
        }

        // 2. Permanent failures are permanent on every attempt: retrying cannot help, which
        //    is exactly why they skip the backoff path entirely.
        if (CauseRules.ClassOf(script.Signature) == FailureClass.Permanent)
        {
            return Fail(script.Signature, payload, job);
        }

        // 3. Transient scripts fail up to and including FailUntilAttempt, then heal.
        if (job.Attempts <= script.FailUntilAttempt)
        {
            logger.LogDebug(
                "Job {JobId} attempt {Attempt} scripted to fail transiently as {Signature}.",
                job.Id,
                job.Attempts,
                script.Signature);

            return Fail(script.Signature, payload, job);
        }

        return ExecutionOutcome.Succeeded();
    }

    private static ExecutionOutcome Fail(string signature, JobPayload payload, Job job)
    {
        var (message, stackTrace) = SyntheticErrorText.Compose(signature, payload, job.DownstreamEndpoint);
        return ExecutionOutcome.Failed(CauseRules.ClassOf(signature), signature, message, stackTrace);
    }
}
