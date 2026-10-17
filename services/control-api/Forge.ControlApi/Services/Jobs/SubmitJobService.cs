using System.Text.Json;
using Forge.ControlApi.Contracts.Jobs;
using Forge.ControlApi.Contracts.Messaging;
using Forge.ControlApi.Persistence.Entities;
using Microsoft.AspNetCore.Http;

namespace Forge.ControlApi.Services.Jobs;

public sealed class SubmitJobService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IJobSubmissionStore _store;

    public SubmitJobService(IJobSubmissionStore store)
    {
        _store = store;
    }

    public async Task<SubmitJobResult> SubmitAsync(
        string clientScopeId,
        SubmitJobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(clientScopeId) ||
            clientScopeId.Length > 512)
        {
            throw new ArgumentException(
                "A valid authenticated client scope is required.",
                nameof(clientScopeId));
        }

        if (request.SubmissionRequestId == Guid.Empty)
        {
            var error = SubmitJobRequestValidator.Validate(request)!;

            return CreateRejection(
                request.SubmissionRequestId,
                error);
        }

        var fingerprint =
            SubmissionRequestFingerprint.Create(request);

        // Fast path: return the definitive result of an earlier request.
        var existing = await _store.FindAsync(
            clientScopeId,
            request.SubmissionRequestId,
            cancellationToken);

        if (existing is not null)
        {
            return ResolveExisting(
                existing,
                fingerprint,
                request.SubmissionRequestId);
        }

        var validationError =
            SubmitJobRequestValidator.Validate(request);

        var decidedAt = await _store.GetDatabaseTimeAsync(
            cancellationToken);

        if (validationError is not null)
        {
            var statusCode =
                GetValidationStatusCode(validationError.Code);

            var rejection = new JobSubmissionRejection(
                request.SubmissionRequestId,
                "Rejected",
                validationError.Code,
                validationError.Message);

            var responseJson = Serialize(rejection);

            var submission = new StoredSubmission(
                clientScopeId,
                request.SubmissionRequestId,
                fingerprint,
                SubmissionOutcome.Rejected.ToString(),
                statusCode,
                responseJson,
                JobId: null,
                DecidedAt: decidedAt);

            var batch = new SubmissionWriteBatch(
                submission,
                Job: null,
                Outbox: null);

            return await CommitAsync(
                batch,
                fingerprint,
                request.SubmissionRequestId,
                cancellationToken);
        }

        var jobId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var job = new JobSubmissionWriteData(
            JobId: jobId,
            JobType: request.JobType,
            Status: JobStatus.Queued.ToString(),
            InputPayloadJson: request.InputPayload?.GetRawText(),
            InputReference: request.InputReference,
            InputRevision: request.InputRevision,
            CreatedAt: decidedAt,
            NextEligibleAt: decidedAt);

        var messagePayload = new JobAvailableV1Payload(
            messageId,
            jobId,
            request.JobType);

        var outbox = new OutboxMessageWriteData(
            OutboxMessageId: messageId,
            JobId: jobId,
            MessageType: OutboxMessageTypes.JobAvailableV1,
            PayloadJson: Serialize(messagePayload),
            CreatedAt: decidedAt,
            NextAttemptAt: decidedAt);

        var receipt = new JobSubmissionReceipt(
            request.SubmissionRequestId,
            jobId,
            "Accepted",
            decidedAt);

        var receiptJson = Serialize(receipt);

        var acceptedSubmission = new StoredSubmission(
            clientScopeId,
            request.SubmissionRequestId,
            fingerprint,
            SubmissionOutcome.Accepted.ToString(),
            StatusCodes.Status202Accepted,
            receiptJson,
            JobId: jobId,
            DecidedAt: decidedAt);

        var acceptedBatch = new SubmissionWriteBatch(
            acceptedSubmission,
            job,
            outbox);

        return await CommitAsync(
            acceptedBatch,
            fingerprint,
            request.SubmissionRequestId,
            cancellationToken);
    }

    private async Task<SubmitJobResult> CommitAsync(
        SubmissionWriteBatch batch,
        string fingerprint,
        Guid submissionRequestId,
        CancellationToken cancellationToken)
    {
        var commit = await _store.SaveAtomicallyAsync(
            batch,
            cancellationToken);

        if (commit.ReplayedExisting)
        {
            return ResolveExisting(
                commit.Submission,
                fingerprint,
                submissionRequestId);
        }

        return new SubmitJobResult(
            commit.Submission.ResponseStatusCode,
            commit.Submission.ResponseBodyJson);
    }

    private static SubmitJobResult ResolveExisting(
        StoredSubmission existing,
        string fingerprint,
        Guid submissionRequestId)
    {
        if (!string.Equals(
                existing.RequestFingerprint,
                fingerprint,
                StringComparison.Ordinal))
        {
            var conflict = new JobSubmissionRejection(
                submissionRequestId,
                "Rejected",
                "IdempotencyKeyConflict",
                "This submissionRequestId was already used with different parameters.");

            return new SubmitJobResult(
                StatusCodes.Status409Conflict,
                Serialize(conflict));
        }

        return new SubmitJobResult(
            existing.ResponseStatusCode,
            existing.ResponseBodyJson);
    }

    private static SubmitJobResult CreateRejection(
        Guid submissionRequestId,
        SubmitJobValidationError error)
    {
        var rejection = new JobSubmissionRejection(
            submissionRequestId,
            "Rejected",
            error.Code,
            error.Message);

        return new SubmitJobResult(
            GetValidationStatusCode(error.Code),
            Serialize(rejection));
    }

    private static int GetValidationStatusCode(string errorCode)
    {
        return errorCode switch
        {
            "UnsupportedJobType" =>
                StatusCodes.Status422UnprocessableEntity,

            "InputPayloadTooLarge" =>
                StatusCodes.Status413PayloadTooLarge,

            _ => StatusCodes.Status400BadRequest
        };
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }
}