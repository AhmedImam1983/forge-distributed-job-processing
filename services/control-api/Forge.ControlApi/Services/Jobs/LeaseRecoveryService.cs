using System.Data;
using System.Text.Json;
using Forge.ControlApi.Contracts.Messaging;
using Forge.ControlApi.Persistence;
using Forge.ControlApi.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Forge.ControlApi.Services.Jobs;

public sealed class LeaseRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly ForgeDbContext _db;
    private readonly LeaseRecoveryOptions _options;

    public LeaseRecoveryService(
        ForgeDbContext db,
        IOptions<LeaseRecoveryOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<int> RecoverExpiredClaimsAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        // Lock jobs first, matching the lock order used by claim and renewal.
        // PostgreSQL's clock is authoritative for lease expiration.
        var candidateJobIds = await _db.Database
            .SqlQuery<Guid>($"""
                SELECT j.job_id AS "Value"
                FROM jobs AS j
                INNER JOIN job_attempts AS a
                    ON a.job_id = j.job_id
                WHERE j.status IN ('Running', 'CancelRequested')
                  AND a.status = 'Running'
                  AND a.fencing_token = j.last_fencing_token
                  AND a.lease_expires_at <= clock_timestamp()
                ORDER BY a.lease_expires_at, j.job_id
                LIMIT {_options.BatchSize}
                FOR UPDATE OF j SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        var recoveredCount = 0;

        foreach (var jobId in candidateJobIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The job row is already locked by the candidate query.
            var job = await _db.Jobs.SingleOrDefaultAsync(
                x => x.JobId == jobId,
                cancellationToken);

            if (job is null ||
                job.Status is not (
                    JobStatus.Running or JobStatus.CancelRequested))
            {
                continue;
            }

            // Keep the same lock order: Job, then JobAttempt.
            var attempt = await _db.JobAttempts
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM job_attempts
                    WHERE job_id = {jobId}
                      AND fencing_token = {job.LastFencingToken}
                      AND status = 'Running'
                    ORDER BY attempt_number DESC
                    LIMIT 1
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken);

            if (attempt is null)
            {
                continue;
            }

            var now = await _db.Database
                .SqlQuery<DateTimeOffset>(
                    $"SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync(cancellationToken);

            // Recheck after taking the locks. Never recover a renewed or
            // otherwise superseded attempt based on an earlier observation.
            if (job.Status is not (
                    JobStatus.Running or JobStatus.CancelRequested) ||
                attempt.Status != JobAttemptStatus.Running ||
                attempt.FencingToken != job.LastFencingToken ||
                attempt.LeaseExpiresAt > now)
            {
                continue;
            }

            attempt.Status = JobAttemptStatus.LeaseExpired;
            attempt.CompletedAt = now;
            attempt.FailureCode = "LEASE_EXPIRED";
            attempt.FailureMessage =
                "The execution lease expired before completion.";

            if (job.Status == JobStatus.CancelRequested)
            {
                // The expired claim is revoked before cancellation is finalized.
                job.Status = JobStatus.Cancelled;
                recoveredCount++;
                continue;
            }

            if (attempt.AttemptNumber >= _options.MaxAttempts)
            {
                job.Status = JobStatus.Failed;
                recoveredCount++;
                continue;
            }

            var retryAt = now.Add(GetRetryDelay(attempt.AttemptNumber));

            job.Status = JobStatus.Queued;
            job.NextEligibleAt = retryAt;

            // Persist the re-availability notification atomically with the
            // state transition. The publisher will honor NextAttemptAt.
            var messageId = Guid.NewGuid();
            var payload = new JobAvailableV1Payload(
                messageId,
                job.JobId,
                job.JobType);

            _db.OutboxMessages.Add(new OutboxMessage
            {
                OutboxMessageId = messageId,
                JobId = job.JobId,
                MessageType = OutboxMessageTypes.JobAvailableV1,
                PayloadJson = JsonSerializer.Serialize(
                    payload,
                    JsonOptions),
                CreatedAt = now,
                NextAttemptAt = retryAt,
                AttemptCount = 0,
                PublishedAt = null
            });

            recoveredCount++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return recoveredCount;
    }

    private TimeSpan GetRetryDelay(int attemptNumber)
    {
        var exponent = Math.Clamp(attemptNumber - 1, 0, 30);
        var multiplier = 1L << exponent;

        var seconds = Math.Min(
            (long)_options.InitialRetryDelaySeconds * multiplier,
            _options.MaxRetryDelaySeconds);

        return TimeSpan.FromSeconds(seconds);
    }
}
