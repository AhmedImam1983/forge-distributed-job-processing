using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.ControlApi.Contracts.Jobs;
using Forge.ControlApi.Contracts.Messaging;
using Forge.ControlApi.Persistence;
using Forge.ControlApi.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Forge.ControlApi.Services.Jobs;

public sealed class CompletionService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly ForgeDbContext _db;
    private readonly LeaseRecoveryOptions _options;

    public CompletionService(
        ForgeDbContext db,
        IOptions<LeaseRecoveryOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<CompleteJobResult> CompleteAsync(
        CompleteJobRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(
                request,
                out var normalized,
                out var validationMessage))
        {
            return Result(
                400,
                new CompleteJobResponse(
                    request.CompletionRequestId,
                    request.JobId,
                    request.AttemptId,
                    "InvalidCompletionRequest",
                    null,
                    "NotFound",
                    "NotFound",
                    false,
                    validationMessage));
        }

        await using var transaction =
            await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        // Serialize concurrent deliveries of the same logical operation.
        await AcquireRequestLockAsync(
            normalized.CompletionRequestId,
            cancellationToken);

        var existing = await _db.CompletionRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.CompletionRequestId ==
                     normalized.CompletionRequestId,
                cancellationToken);

        if (existing is not null)
        {
            // Always lock Job before Attempt, consistent with renewal
            // and recovery. Report present state independently.
            var current = await LoadCurrentStatusesAsync(
                existing.JobId,
                existing.AttemptId,
                cancellationToken);

            if (!string.Equals(
                    existing.RequestFingerprint,
                    normalized.RequestFingerprint,
                    StringComparison.Ordinal))
            {
                var previousConflict = await _db.CompletionRequestConflicts
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        x => x.CompletionRequestId ==
                                 existing.CompletionRequestId &&
                             x.RequestFingerprint ==
                                 normalized.RequestFingerprint,
                        cancellationToken);

                if (previousConflict is not null)
                {
                    var replayedConflict = new CompleteJobResponse(
                        existing.CompletionRequestId,
                        existing.JobId,
                        existing.AttemptId,
                        CompletionOutcome.IdempotencyKeyConflict.ToString(),
                        existing.Outcome.ToString(),
                        current.JobStatus,
                        current.AttemptStatus,
                        true,
                        "Original idempotency conflict decision returned.");

                    await transaction.CommitAsync(cancellationToken);
                    return Result(409, replayedConflict);
                }

                var conflictTime =
                    await GetDatabaseTimeAsync(cancellationToken);

                _db.CompletionRequestConflicts.Add(
                    new CompletionRequestConflict
                    {
                        CompletionRequestId =
                            existing.CompletionRequestId,
                        RequestFingerprint =
                            normalized.RequestFingerprint,
                        RequestPayloadJson =
                            normalized.RequestPayloadJson,
                        Outcome = CompletionOutcome.IdempotencyKeyConflict,
                        DecidedAt = conflictTime
                    });

                await _db.SaveChangesAsync(cancellationToken);

                var conflict = new CompleteJobResponse(
                    existing.CompletionRequestId,
                    existing.JobId,
                    existing.AttemptId,
                    CompletionOutcome.IdempotencyKeyConflict.ToString(),
                    existing.Outcome.ToString(),
                    current.JobStatus,
                    current.AttemptStatus,
                    false,
                    "CompletionRequestId was already used with different parameters.");

                await transaction.CommitAsync(cancellationToken);
                return Result(409, conflict);
            }

            var replay = new CompleteJobResponse(
                existing.CompletionRequestId,
                existing.JobId,
                existing.AttemptId,
                existing.Outcome.ToString(),
                null,
                current.JobStatus,
                current.AttemptStatus,
                true,
                "Original completion decision returned.");

            await transaction.CommitAsync(cancellationToken);
            return Result(GetHttpStatus(existing.Outcome), replay);
        }

        // Lock order is important: Job, then JobAttempt.
        var job = await _db.Jobs
            .FromSqlInterpolated(
                $"SELECT * FROM jobs WHERE job_id = {normalized.JobId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (job is null)
        {
            var notFoundTime =
                await GetDatabaseTimeAsync(cancellationToken);

            return await PersistDecisionAsync(
                normalized,
                CompletionOutcome.NotFound,
                notFoundTime,
                "NotFound",
                "NotFound",
                "Job was not found.",
                cancellationToken);
        }

        var attempt = await _db.JobAttempts
            .FromSqlInterpolated(
                $"SELECT * FROM job_attempts WHERE attempt_id = {normalized.AttemptId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        // Capture the authoritative decision time after locking both rows.
        var now = await GetDatabaseTimeAsync(cancellationToken);

        if (attempt is null || attempt.JobId != job.JobId)
        {
            return await PersistDecisionAsync(
                normalized,
                CompletionOutcome.NotFound,
                now,
                job.Status.ToString(),
                "NotFound",
                "Execution attempt was not found for this job.",
                cancellationToken);
        }

        if (attempt.Status != JobAttemptStatus.Running)
        {
            return await PersistDecisionAsync(
                normalized,
                CompletionOutcome.AttemptAlreadyFinalized,
                now,
                job.Status.ToString(),
                attempt.Status.ToString(),
                "The execution attempt is already finalized.",
                cancellationToken);
        }

        var ownerMatches =
            attempt.WorkerInstanceId == normalized.WorkerInstanceId &&
            attempt.FencingToken == normalized.FencingToken &&
            job.LastFencingToken == normalized.FencingToken;

        if (!ownerMatches)
        {
            return await PersistDecisionAsync(
                normalized,
                CompletionOutcome.NotOwnerOrSuperseded,
                now,
                job.Status.ToString(),
                attempt.Status.ToString(),
                "The worker no longer owns the current execution claim.",
                cancellationToken);
        }

        // Cancellation wins when CancelRequested was recorded first.
        if (job.Status == JobStatus.CancelRequested &&
            normalized.Outcome != "Cancelled")
        {
            return await PersistDecisionAsync(
                normalized,
                CompletionOutcome.CancellationPending,
                now,
                job.Status.ToString(),
                attempt.Status.ToString(),
                "Cancellation must be confirmed by a separate completion request.",
                cancellationToken);
        }

        if (normalized.Outcome == "Cancelled")
        {
            if (job.Status != JobStatus.CancelRequested)
            {
                return await PersistDecisionAsync(
                    normalized,
                    CompletionOutcome.InvalidOutcomeForState,
                    now,
                    job.Status.ToString(),
                    attempt.Status.ToString(),
                    "Cancelled completion requires CancelRequested job state.",
                    cancellationToken);
            }
        }
        else if (job.Status != JobStatus.Running)
        {
            return await PersistDecisionAsync(
                normalized,
                CompletionOutcome.InvalidOutcomeForState,
                now,
                job.Status.ToString(),
                attempt.Status.ToString(),
                "This completion outcome is not permitted in the current job state.",
                cancellationToken);
        }

        // Do not accept a completion from an expired claim. The database
        // clock is checked after both rows have been locked.
        if (now >= attempt.LeaseExpiresAt)
        {
            return await PersistDecisionAsync(
                normalized,
                CompletionOutcome.LeaseExpiredOrLost,
                now,
                job.Status.ToString(),
                attempt.Status.ToString(),
                "The execution lease expired before completion was accepted.",
                cancellationToken);
        }

        CompletionOutcome acceptedOutcome;

        switch (normalized.Outcome)
        {
            case "Succeeded":
                job.Status = JobStatus.Succeeded;
                job.ResultDescriptorJson =
                    normalized.ResultDescriptor!.Value.GetRawText();

                attempt.Status = JobAttemptStatus.Succeeded;
                attempt.CompletedAt = now;
                attempt.FailureCode = null;
                attempt.FailureMessage = null;

                acceptedOutcome = CompletionOutcome.AcceptedSucceeded;
                break;

            case "Failed":
                attempt.Status = JobAttemptStatus.Failed;
                attempt.CompletedAt = now;
                attempt.FailureCode = normalized.FailureCode;
                attempt.FailureMessage = normalized.FailureMessage;

                var retryAllowed =
                    _options.RetryableFailureCodes?.Contains(
                        normalized.FailureCode!,
                        StringComparer.Ordinal) == true;

                if (retryAllowed &&
                    attempt.AttemptNumber < _options.MaxAttempts)
                {
                    var retryAt = now.Add(
                        GetRetryDelay(attempt.AttemptNumber));

                    job.Status = JobStatus.Queued;
                    job.NextEligibleAt = retryAt;

                    // Job state and its availability notification are
                    // committed together with the completion decision.
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

                    acceptedOutcome =
                        CompletionOutcome.AcceptedRetryScheduled;
                }
                else
                {
                    job.Status = JobStatus.Failed;
                    acceptedOutcome = CompletionOutcome.AcceptedFailed;
                }

                break;

            case "Cancelled":
                job.Status = JobStatus.Cancelled;
                attempt.Status = JobAttemptStatus.Cancelled;
                attempt.CompletedAt = now;
                attempt.FailureCode = null;
                attempt.FailureMessage = null;

                acceptedOutcome = CompletionOutcome.AcceptedCancelled;
                break;

            default:
                // TryNormalize rejects unsupported outcome strings.
                throw new InvalidOperationException(
                    "Normalized completion outcome was unsupported.");
        }

        return await PersistDecisionAsync(
            normalized,
            acceptedOutcome,
            now,
            job.Status.ToString(),
            attempt.Status.ToString(),
            null,
            cancellationToken);
    }

    private async Task<CompleteJobResult> PersistDecisionAsync(
        NormalizedCompletion request,
        CompletionOutcome outcome,
        DateTimeOffset decidedAt,
        string currentJobStatus,
        string currentAttemptStatus,
        string? message,
        CancellationToken cancellationToken)
    {
        _db.CompletionRequests.Add(new CompletionRequest
        {
            CompletionRequestId = request.CompletionRequestId,
            JobId = request.JobId,
            AttemptId = request.AttemptId,
            WorkerInstanceId = request.WorkerInstanceId,
            FencingToken = request.FencingToken,
            RequestFingerprint = request.RequestFingerprint,
            RequestPayloadJson = request.RequestPayloadJson,
            Outcome = outcome,
            DecidedAt = decidedAt
        });

        var response = new CompleteJobResponse(
            request.CompletionRequestId,
            request.JobId,
            request.AttemptId,
            outcome.ToString(),
            null,
            currentJobStatus,
            currentAttemptStatus,
            false,
            message);

        await _db.SaveChangesAsync(cancellationToken);
        await _db.Database.CurrentTransaction!
            .CommitAsync(cancellationToken);

        return Result(GetHttpStatus(outcome), response);
    }

    private async Task<CurrentStatuses> LoadCurrentStatusesAsync(
        Guid jobId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        var job = await _db.Jobs
            .FromSqlInterpolated(
                $"SELECT * FROM jobs WHERE job_id = {jobId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (job is null)
        {
            return new CurrentStatuses("NotFound", "NotFound");
        }

        var attempt = await _db.JobAttempts
            .FromSqlInterpolated(
                $"SELECT * FROM job_attempts WHERE attempt_id = {attemptId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (attempt is null || attempt.JobId != jobId)
        {
            return new CurrentStatuses(
                job.Status.ToString(),
                "NotFound");
        }

        return new CurrentStatuses(
            job.Status.ToString(),
            attempt.Status.ToString());
    }

    private async Task AcquireRequestLockAsync(
        Guid completionRequestId,
        CancellationToken cancellationToken)
    {
        await using var command =
            _db.Database.GetDbConnection().CreateCommand();

        command.Transaction = _db.Database.CurrentTransaction!
            .GetDbTransaction();

        command.CommandText =
            "SELECT pg_advisory_xact_lock(" +
            "hashtextextended(@completion_request_id, 0))";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "completion_request_id";
        parameter.Value = completionRequestId.ToString("D");
        command.Parameters.Add(parameter);

        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<DateTimeOffset> GetDatabaseTimeAsync(
        CancellationToken cancellationToken)
    {
        return await _db.Database
            .SqlQuery<DateTimeOffset>(
                $"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(cancellationToken);
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

    private static int GetHttpStatus(CompletionOutcome outcome)
    {
        return outcome switch
        {
            CompletionOutcome.AcceptedSucceeded => 200,
            CompletionOutcome.AcceptedRetryScheduled => 200,
            CompletionOutcome.AcceptedFailed => 200,
            CompletionOutcome.AcceptedCancelled => 200,
            CompletionOutcome.NotFound => 404,
            _ => 409
        };
    }

    private static bool TryNormalize(
        CompleteJobRequest request,
        out NormalizedCompletion normalized,
        out string error)
    {
        normalized = default!;
        error = string.Empty;

        var workerId = request.WorkerInstanceId?.Trim() ?? string.Empty;
        var outcome = request.Outcome?.Trim() ?? string.Empty;

        if (request.CompletionRequestId == Guid.Empty ||
            request.JobId == Guid.Empty ||
            request.AttemptId == Guid.Empty ||
            workerId.Length is < 1 or > 200 ||
            request.FencingToken <= 0)
        {
            error = "Identifiers must be non-empty, WorkerInstanceId must " +
                    "contain 1 to 200 characters, and FencingToken must be positive.";
            return false;
        }

        if (outcome is not ("Succeeded" or "Failed" or "Cancelled"))
        {
            error = "Outcome must be Succeeded, Failed, or Cancelled.";
            return false;
        }

        JsonElement? resultDescriptor =
            request.ResultDescriptor.HasValue
                ? request.ResultDescriptor.Value.Clone()
                : null;

        var failureCode = string.IsNullOrWhiteSpace(request.FailureCode)
            ? null
            : request.FailureCode.Trim().ToUpperInvariant();

        var failureMessage = NullIfWhiteSpace(request.FailureMessage);
        var cancellationDetails = NullIfWhiteSpace(
            request.CancellationDetails);

        if (failureCode is { Length: > 100 } ||
            failureMessage is { Length: > 2000 } ||
            cancellationDetails is { Length: > 2000 })
        {
            error = "Failure and cancellation details exceed their size limits.";
            return false;
        }

        switch (outcome)
        {
            case "Succeeded":
                if (!resultDescriptor.HasValue ||
                    resultDescriptor.Value.ValueKind != JsonValueKind.Object)
                {
                    error = "Succeeded completion requires an object ResultDescriptor.";
                    return false;
                }

                if (Encoding.UTF8.GetByteCount(
                        resultDescriptor.Value.GetRawText()) > 65536)
                {
                    error = "ResultDescriptor must not exceed 65536 UTF-8 bytes.";
                    return false;
                }

                if (failureCode is not null ||
                    failureMessage is not null ||
                    cancellationDetails is not null)
                {
                    error = "Succeeded completion cannot include failure or cancellation details.";
                    return false;
                }

                break;

            case "Failed":
                if (resultDescriptor.HasValue ||
                    failureCode is null ||
                    !Regex.IsMatch(
                        failureCode,
                        "^[A-Z][A-Z0-9_]{0,99}$",
                        RegexOptions.CultureInvariant) ||
                    failureMessage is null ||
                    cancellationDetails is not null)
                {
                    error = "Failed completion requires a valid failureCode and failureMessage, " +
                            "and cannot include a result descriptor or cancellation details.";
                    return false;
                }

                break;

            case "Cancelled":
                if (resultDescriptor.HasValue ||
                    failureCode is not null ||
                    failureMessage is not null)
                {
                    error = "Cancelled completion cannot include a result descriptor or failure details.";
                    return false;
                }

                break;
        }

        var payloadJson = JsonSerializer.Serialize(
            new CompletionPayload(
                request.JobId,
                request.AttemptId,
                workerId,
                request.FencingToken,
                outcome,
                resultDescriptor,
                failureCode,
                failureMessage,
                cancellationDetails),
            JsonOptions);

        var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)))
            .ToLowerInvariant();

        normalized = new NormalizedCompletion(
            request.CompletionRequestId,
            request.JobId,
            request.AttemptId,
            workerId,
            request.FencingToken,
            outcome,
            resultDescriptor,
            failureCode,
            failureMessage,
            cancellationDetails,
            payloadJson,
            fingerprint);

        return true;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static CompleteJobResult Result(
        int statusCode,
        CompleteJobResponse response)
    {
        return new CompleteJobResult(
            statusCode,
            JsonSerializer.Serialize(response, JsonOptions));
    }

    private sealed record CompletionPayload(
        Guid JobId,
        Guid AttemptId,
        string WorkerInstanceId,
        long FencingToken,
        string Outcome,
        JsonElement? ResultDescriptor,
        string? FailureCode,
        string? FailureMessage,
        string? CancellationDetails);

    private sealed record CurrentStatuses(
        string JobStatus,
        string AttemptStatus);

    private sealed record NormalizedCompletion(
        Guid CompletionRequestId,
        Guid JobId,
        Guid AttemptId,
        string WorkerInstanceId,
        long FencingToken,
        string Outcome,
        JsonElement? ResultDescriptor,
        string? FailureCode,
        string? FailureMessage,
        string? CancellationDetails,
        string RequestPayloadJson,
        string RequestFingerprint);
}
