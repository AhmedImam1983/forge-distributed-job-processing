using Forge.ControlApi.Persistence.Entities;
using Forge.ControlApi.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Forge.ControlApi.Persistence;

public sealed class PostgresJobSubmissionStore : IJobSubmissionStore
{
    private readonly ForgeDbContext _dbContext;

    public PostgresJobSubmissionStore(ForgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<StoredSubmission?> FindAsync(
        string clientScopeId,
        Guid submissionRequestId,
        CancellationToken cancellationToken)
    {
        return await FindExistingAsync(
            clientScopeId,
            submissionRequestId,
            cancellationToken);
    }
public Task<DateTimeOffset> GetDatabaseTimeAsync(
    CancellationToken cancellationToken)
{
    return _dbContext.Database
        .SqlQuery<DateTimeOffset>(
            $"SELECT CURRENT_TIMESTAMP AS \"Value\"")
        .SingleAsync(cancellationToken);
}
    public async Task<SubmissionStoreCommitResult> SaveAtomicallyAsync(
        SubmissionWriteBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        ValidateBatch(batch);

        var submission = batch.Submission;

        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(
                cancellationToken);

        // Check again inside the transaction to handle retries and races.
        var existing = await FindExistingAsync(
            submission.ClientScopeId,
            submission.SubmissionRequestId,
            cancellationToken);

        if (existing is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None);

            return new SubmissionStoreCommitResult(
                existing,
                ReplayedExisting: true);
        }

        try
        {
            if (batch.Job is { } jobData)
            {
                _dbContext.Jobs.Add(new Job
                {
                    JobId = jobData.JobId,
                    JobType = jobData.JobType,
                    Status = Enum.Parse<JobStatus>(
                        jobData.Status,
                        ignoreCase: false),
                    InputPayloadJson = jobData.InputPayloadJson,
                    InputReference = jobData.InputReference,
                    InputRevision = jobData.InputRevision,
                    CreatedAt = jobData.CreatedAt,
                    NextEligibleAt = jobData.NextEligibleAt
                });
            }

            if (batch.Outbox is { } outboxData)
            {
                _dbContext.OutboxMessages.Add(new OutboxMessage
                {
                    OutboxMessageId = outboxData.OutboxMessageId,
                    JobId = outboxData.JobId,
                    MessageType = outboxData.MessageType,
                    PayloadJson = outboxData.PayloadJson,
                    CreatedAt = outboxData.CreatedAt,
                    NextAttemptAt = outboxData.NextAttemptAt,
                    AttemptCount = 0,
                    PublishedAt = null
                });
            }

            _dbContext.SubmissionRequests.Add(new SubmissionRequest
            {
                ClientScopeId = submission.ClientScopeId,
                SubmissionRequestId = submission.SubmissionRequestId,
                RequestFingerprint = submission.RequestFingerprint,
                Outcome = Enum.Parse<SubmissionOutcome>(
                    submission.Outcome,
                    ignoreCase: false),
                ResponseStatusCode = submission.ResponseStatusCode,
                ResponseBodyJson = submission.ResponseBodyJson,
                JobId = submission.JobId,
                DecidedAt = submission.DecidedAt
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new SubmissionStoreCommitResult(
                submission,
                ReplayedExisting: false);
        }
        catch (DbUpdateException exception)
            when (IsSubmissionKeyConflict(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None);

            // Remove pending entities before querying for the winner.
            _dbContext.ChangeTracker.Clear();

            var winner = await FindExistingAsync(
                submission.ClientScopeId,
                submission.SubmissionRequestId,
                cancellationToken);

            if (winner is null)
            {
                throw;
            }

            return new SubmissionStoreCommitResult(
                winner,
                ReplayedExisting: true);
        }
    }

    private Task<StoredSubmission?> FindExistingAsync(
        string clientScopeId,
        Guid submissionRequestId,
        CancellationToken cancellationToken)
    {
        return _dbContext.SubmissionRequests
            .AsNoTracking()
            .Where(x =>
                x.ClientScopeId == clientScopeId &&
                x.SubmissionRequestId == submissionRequestId)
            .Select(x => new StoredSubmission(
                x.ClientScopeId,
                x.SubmissionRequestId,
                x.RequestFingerprint,
                x.Outcome.ToString(),
                x.ResponseStatusCode,
                x.ResponseBodyJson,
                x.JobId,
                x.DecidedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static void ValidateBatch(SubmissionWriteBatch batch)
    {
        var submission = batch.Submission;

        if (string.IsNullOrWhiteSpace(submission.ClientScopeId) ||
            submission.ClientScopeId.Length > 512)
        {
            throw new ArgumentException(
                "A valid client scope is required.",
                nameof(batch));
        }

        var accepted = string.Equals(
            submission.Outcome,
            nameof(SubmissionOutcome.Accepted),
            StringComparison.Ordinal);

        var rejected = string.Equals(
            submission.Outcome,
            nameof(SubmissionOutcome.Rejected),
            StringComparison.Ordinal);

        if (!accepted && !rejected)
        {
            throw new ArgumentException(
                "Unknown submission outcome.",
                nameof(batch));
        }

        if (accepted)
        {
            if (submission.JobId is null ||
                batch.Job is null ||
                batch.Outbox is null ||
                batch.Job.JobId != submission.JobId ||
                batch.Outbox.JobId != submission.JobId)
            {
                throw new ArgumentException(
                    "An accepted submission must include its matching job and outbox message.",
                    nameof(batch));
            }
        }
        else if (submission.JobId is not null ||
                 batch.Job is not null ||
                 batch.Outbox is not null)
        {
            throw new ArgumentException(
                "A rejected submission must not create a job or outbox message.",
                nameof(batch));
        }
    }

    private static bool IsSubmissionKeyConflict(
        DbUpdateException exception)
    {
        return exception.GetBaseException() is PostgresException postgres &&
               postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
               string.Equals(
                   postgres.ConstraintName,
                   "pk_submission_requests",
                   StringComparison.Ordinal);
    }
}
