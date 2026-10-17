using System;
using System.Threading;
using System.Threading.Tasks;

namespace Forge.ControlApi.Services.Jobs;

public interface IJobSubmissionStore
{
    Task<StoredSubmission?> FindAsync(
        string clientScopeId,
        Guid submissionRequestId,
        CancellationToken cancellationToken);

    Task<SubmissionStoreCommitResult> SaveAtomicallyAsync(
        SubmissionWriteBatch batch,
        CancellationToken cancellationToken);

    Task<DateTimeOffset> GetDatabaseTimeAsync(
    CancellationToken cancellationToken);
}

public sealed record StoredSubmission(
    string ClientScopeId,
    Guid SubmissionRequestId,
    string RequestFingerprint,
    string Outcome,
    int ResponseStatusCode,
    string ResponseBodyJson,
    Guid? JobId,
    DateTimeOffset DecidedAt);

public sealed record JobSubmissionWriteData(
    Guid JobId,
    string JobType,
    string Status,
    string? InputPayloadJson,
    string? InputReference,
    string? InputRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextEligibleAt);

public sealed record OutboxMessageWriteData(
    Guid OutboxMessageId,
    Guid JobId,
    string MessageType,
    string PayloadJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt);

public sealed record SubmissionWriteBatch(
    StoredSubmission Submission,
    JobSubmissionWriteData? Job,
    OutboxMessageWriteData? Outbox);

public sealed record SubmissionStoreCommitResult(
    StoredSubmission Submission,
    bool ReplayedExisting);
