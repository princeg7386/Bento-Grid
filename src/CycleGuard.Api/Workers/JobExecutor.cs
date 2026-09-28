using CycleGuard.Api.Domain;
using CycleGuard.Api.Downstream;
using CycleGuard.Api.Queue;

namespace CycleGuard.Api.Workers;

/// <summary>
/// Runs one claimed job and routes the result: success, retry with backoff, or dead-letter.
/// The transient/permanent split happens here and nowhere else.
/// </summary>
public sealed class JobExecutor(
    JobQueue queue,
    IDownstreamGateway gateway,
    RawErrorVault rawErrors,
    ILogger<JobExecutor> logger)
{
    public async Task ExecuteAsync(Job job, string workerId, CancellationToken cancellationToken)
    {
        var attempt = await queue.RecordAttemptStartAsync(job, workerId, cancellationToken);

        ExecutionOutcome outcome;
        try
        {
            outcome = await gateway.ExecuteAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown. Leave the lease to expire so another worker picks the job up.
            throw;
        }
        catch (Exception exception)
        {
            // An unexpected exception is treated as transient: we do not know that retrying
            // cannot help. The raw text never reaches the database; only the masked form does.
            outcome = ExecutionOutcome.Failed(
                FailureClass.Transient,
                FailureSignatures.Unknown,
                exception.Message,
                exception.ToString());
        }

        if (outcome.Success)
        {
            var completion = await queue.MarkSucceededAsync(job, attempt.Id, workerId, cancellationToken);
            if (completion.Committed && completion.DuplicatePrevented)
            {
                logger.LogInformation(
                    "Job {JobId} completed but the downstream already had key {Key}; no second effect applied.",
                    job.Id,
                    job.DownstreamIdempotencyKey);
            }

            return;
        }

        // Raw synthetic text is kept in memory only, so the drawer can show raw versus masked.
        rawErrors.Store(attempt.Id, outcome.RawMessage, outcome.RawStackTrace);

        if (outcome.Class == FailureClass.Permanent)
        {
            await queue.DeadLetterAsync(
                job,
                attempt.Id,
                workerId,
                outcome,
                "Permanent failure, so no retries were attempted.",
                cancellationToken);
            return;
        }

        if (job.Attempts >= job.MaxAttempts)
        {
            await queue.DeadLetterAsync(
                job,
                attempt.Id,
                workerId,
                outcome,
                $"Retries exhausted after {job.Attempts} attempt(s).",
                cancellationToken);
            return;
        }

        await queue.ScheduleRetryAsync(job, attempt.Id, workerId, outcome, cancellationToken);
    }
}
