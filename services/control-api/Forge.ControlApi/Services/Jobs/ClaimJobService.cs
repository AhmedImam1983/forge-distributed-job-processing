using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.ControlApi.Contracts.Jobs;
using Forge.ControlApi.Persistence;
using Forge.ControlApi.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Forge.ControlApi.Services.Jobs;

public sealed class ClaimJobService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly ForgeDbContext _db;
    private readonly JobClaimOptions _options;

    public ClaimJobService(
        ForgeDbContext db,
        IOptions<JobClaimOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<ClaimJobResult> ClaimAsync(
        ClaimJobRequest request,
        CancellationToken cancellationToken)
    {
        var workerInstanceId = request.WorkerInstanceId?.Trim();

        if (request.JobId == Guid.Empty ||
            request.ClaimRequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(workerInstanceId) ||
            workerInstanceId.Length > 200)
        {
            return CreateResult(
                400,
                new ClaimJobResponse(
                    request.ClaimRequestId,
                    request.JobId,
                    "InvalidClaimRequest",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "JobId and ClaimRequestId must be non-empty GUIDs. " +
                    "WorkerInstanceId must contain 1 to 200 characters."));
        }

        var fingerprint = CreateFingerprint(
            request.JobId,
            workerInstanceId);

        await using var transaction =
            await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        // Serialize requests using the same idempotency key.
        // A hash collision only causes extra serialization; the actual
        // identity is always checked using ClaimRequestId in PostgreSQL.
        await AcquireRequestLockAsync(
            request.ClaimRequestId,
            cancellationToken);

        var existing = await _db.ClaimRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.ClaimRequestId == request.ClaimRequestId,
                cancellationToken);

        if (existing is not null)
        {
            if (!string.Equals(
                    existing.RequestFingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);

                return CreateResult(
                    409,
                    new ClaimJobResponse(
                        request.ClaimRequestId,
                        request.JobId,
                        "IdempotencyKeyConflict",
                        existing.Outcome.ToString(),
                        null,
                        null,
                        null,
                        null,
                        null,
                        "ClaimRequestId was already used with different parameters."));
            }

            var replay = await ResolveReplayAsync(
                existing,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        // Locking the job serializes different ClaimRequestIds
        // attempting to claim the same job.
        var job = await LoadJobForUpdateAsync(
            request.JobId,
            cancellationToken);

        var now = await GetDatabaseTimeAsync(cancellationToken);

        if (job is null)
        {
            return await SaveDecisionAsync(
                CreateRequestRecord(
                    request,
                    workerInstanceId,
                    fingerprint,
                    ClaimRequestOutcome.NotFound,
                    now),
                new ClaimJobResponse(
                    request.ClaimRequestId,
                    request.JobId,
                    "NotFound",
                    null,
                    "NotFound",
                    null,
                    null,
                    null,
                    null),
                404,
                cancellationToken);
        }

        // A Running job cannot be claimed again until the control process
        // has durably recovered the expired attempt.
        if (job.Status == JobStatus.Running)
        {
            return await SaveDecisionAsync(
                CreateRequestRecord(
                    request,
                    workerInstanceId,
                    fingerprint,
                    ClaimRequestOutcome.AlreadyClaimed,
                    now),
                new ClaimJobResponse(
                    request.ClaimRequestId,
                    request.JobId,
                    "AlreadyClaimed",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
                409,
                cancellationToken);
        }

        if (job.Status != JobStatus.Queued)
        {
            return await SaveDecisionAsync(
                CreateRequestRecord(
                    request,
                    workerInstanceId,
                    fingerprint,
                    ClaimRequestOutcome.NotEligible,
                    now),
                new ClaimJobResponse(
                    request.ClaimRequestId,
                    request.JobId,
                    "NotEligible",
                    null,
                    "NotClaimed",
                    null,
                    null,
                    null,
                    null),
                409,
                cancellationToken);
        }

        // Defensive consistency check. A Queued job must not acquire
        // another attempt while an earlier attempt remains Running.
        var runningAttemptExists = await _db.JobAttempts
            .AsNoTracking()
            .AnyAsync(
                x => x.JobId == job.JobId &&
                     x.Status == JobAttemptStatus.Running,
                cancellationToken);

        now = await GetDatabaseTimeAsync(cancellationToken);

        if (runningAttemptExists)
        {
            return await SaveDecisionAsync(
                CreateRequestRecord(
                    request,
                    workerInstanceId,
                    fingerprint,
                    ClaimRequestOutcome.AlreadyClaimed,
                    now),
                new ClaimJobResponse(
                    request.ClaimRequestId,
                    request.JobId,
                    "AlreadyClaimed",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
                409,
                cancellationToken);
        }

        if (job.NextEligibleAt > now)
        {
            return await SaveDecisionAsync(
                CreateRequestRecord(
                    request,
                    workerInstanceId,
                    fingerprint,
                    ClaimRequestOutcome.NotEligible,
                    now),
                new ClaimJobResponse(
                    request.ClaimRequestId,
                    request.JobId,
                    "NotEligible",
                    null,
                    "NotClaimed",
                    null,
                    null,
                    null,
                    null),
                409,
                cancellationToken);
        }

        var lastAttemptNumber = await _db.JobAttempts
            .Where(x => x.JobId == job.JobId)
            .Select(x => (int?)x.AttemptNumber)
            .MaxAsync(cancellationToken) ?? 0;

        var attemptNumber = checked(lastAttemptNumber + 1);
        var fencingToken = checked(job.LastFencingToken + 1);
        var leaseExpiresAt =
            now.AddSeconds(_options.LeaseDurationSeconds);

        var attempt = new JobAttempt
        {
            AttemptId = Guid.NewGuid(),
            JobId = job.JobId,
            AttemptNumber = attemptNumber,
            WorkerInstanceId = workerInstanceId,
            FencingToken = fencingToken,
            Status = JobAttemptStatus.Running,
            ClaimedAt = now,
            LeaseExpiresAt = leaseExpiresAt
        };

        // The job row remains locked until the transaction commits.
        job.Status = JobStatus.Running;
        job.LastFencingToken = fencingToken;

        var claimRecord = CreateRequestRecord(
            request,
            workerInstanceId,
            fingerprint,
            ClaimRequestOutcome.Granted,
            now,
            attempt.AttemptId,
            fencingToken,
            leaseExpiresAt);

        _db.JobAttempts.Add(attempt);

        var response = new ClaimJobResponse(
            request.ClaimRequestId,
            job.JobId,
            "Granted",
            null,
            "Valid",
            attempt.AttemptId,
            fencingToken,
            leaseExpiresAt,
            CreateExecutionDescriptor(job));

        return await SaveDecisionAsync(
            claimRecord,
            response,
            200,
            cancellationToken);
    }

    private async Task<ClaimJobResult> ResolveReplayAsync(
        ClaimRequest existing,
        CancellationToken cancellationToken)
    {
        if (existing.Outcome != ClaimRequestOutcome.Granted)
        {
            var originalOutcome = existing.Outcome.ToString();

            return CreateResult(
                GetHttpStatusCode(existing.Outcome),
                new ClaimJobResponse(
                    existing.ClaimRequestId,
                    existing.JobId,
                    originalOutcome,
                    null,
                    existing.Outcome == ClaimRequestOutcome.NotFound
                        ? "NotFound"
                        : null,
                    existing.AttemptId,
                    existing.FencingToken,
                    existing.LeaseExpiresAt,
                    null));
        }

        var job = await LoadJobForUpdateAsync(
            existing.JobId,
            cancellationToken);

        var attempt = existing.AttemptId is Guid attemptId
            ? await _db.JobAttempts
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.AttemptId == attemptId,
                    cancellationToken)
            : null;

        var now = await GetDatabaseTimeAsync(cancellationToken);

        var currentStatus = GetCurrentClaimStatus(
            existing,
            job,
            attempt,
            now);

        var isValid = currentStatus == "Valid";

        var response = new ClaimJobResponse(
            existing.ClaimRequestId,
            existing.JobId,
            isValid ? "Granted" : "ExpiredOrLost",
            isValid ? null : "Granted",
            currentStatus,
            existing.AttemptId,
            existing.FencingToken,
            // Return the original expiry recorded for this claim request.
            // CurrentClaimStatus is calculated separately using the current attempt.
            existing.LeaseExpiresAt,
            isValid && job is not null
                ? CreateExecutionDescriptor(job)
                : null);

        return CreateResult(isValid ? 200 : 409, response);
    }

    private static string GetCurrentClaimStatus(
        ClaimRequest claim,
        Job? job,
        JobAttempt? attempt,
        DateTimeOffset now)
    {
        if (job is null)
        {
            return "NotFound";
        }

        if (job.Status == JobStatus.CancelRequested)
        {
            return "CancellationPending";
        }

        if (attempt is null)
        {
            return "Superseded";
        }

        if (job.Status != JobStatus.Running ||
            attempt.Status != JobAttemptStatus.Running ||
            attempt.WorkerInstanceId != claim.WorkerInstanceId ||
            claim.FencingToken != attempt.FencingToken ||
            job.LastFencingToken != attempt.FencingToken)
        {
            return "Superseded";
        }

        if (now >= attempt.LeaseExpiresAt)
        {
            return "Expired";
        }

        return "Valid";
    }

    private ClaimRequest CreateRequestRecord(
        ClaimJobRequest request,
        string workerInstanceId,
        string fingerprint,
        ClaimRequestOutcome outcome,
        DateTimeOffset now,
        Guid? attemptId = null,
        long? fencingToken = null,
        DateTimeOffset? leaseExpiresAt = null)
    {
        return new ClaimRequest
        {
            ClaimRequestId = request.ClaimRequestId,
            JobId = request.JobId,
            WorkerInstanceId = workerInstanceId,
            RequestFingerprint = fingerprint,
            Outcome = outcome,
            AttemptId = attemptId,
            FencingToken = fencingToken,
            LeaseExpiresAt = leaseExpiresAt,
            DecidedAt = now
        };
    }

    private async Task<ClaimJobResult> SaveDecisionAsync(
        ClaimRequest record,
        ClaimJobResponse response,
        int statusCode,
        CancellationToken cancellationToken)
    {
        _db.ClaimRequests.Add(record);

        await _db.SaveChangesAsync(cancellationToken);
        await _db.Database.CurrentTransaction!
            .CommitAsync(cancellationToken);

        return CreateResult(statusCode, response);
    }

    private async Task<Job?> LoadJobForUpdateAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        return await _db.Jobs
            .FromSqlInterpolated(
                $"SELECT * FROM jobs WHERE job_id = {jobId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<DateTimeOffset> GetDatabaseTimeAsync(
        CancellationToken cancellationToken)
    {
        return await _db.Database
            .SqlQuery<DateTimeOffset>(
                $"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(cancellationToken);
    }

    private async Task AcquireRequestLockAsync(
        Guid claimRequestId,
        CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();

        command.Transaction = _db.Database.CurrentTransaction!
            .GetDbTransaction();

        command.CommandText =
            "SELECT pg_advisory_xact_lock(" +
            "hashtextextended(@claim_request_id, 0))";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "claim_request_id";
        parameter.Value = claimRequestId.ToString("D");
        command.Parameters.Add(parameter);

        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static string CreateFingerprint(
        Guid jobId,
        string workerInstanceId)
    {
        var canonical =
            $"{jobId:D}\n{workerInstanceId}";

        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static JobExecutionDescriptor CreateExecutionDescriptor(
        Job job)
    {
        JsonElement? payload = null;

        if (job.InputPayloadJson is not null)
        {
            using var document = JsonDocument.Parse(
                job.InputPayloadJson);

            payload = document.RootElement.Clone();
        }

        return new JobExecutionDescriptor(
            job.JobType,
            payload,
            job.InputReference,
            job.InputRevision);
    }

    private static int GetHttpStatusCode(
        ClaimRequestOutcome outcome)
    {
        return outcome switch
        {
            ClaimRequestOutcome.Granted => 200,
            ClaimRequestOutcome.NotFound => 404,
            _ => 409
        };
    }

    private static ClaimJobResult CreateResult(
        int statusCode,
        ClaimJobResponse response)
    {
        return new ClaimJobResult(
            statusCode,
            JsonSerializer.Serialize(response, JsonOptions));
    }
}
