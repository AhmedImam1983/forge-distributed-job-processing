using System.Data;
using System.Globalization;
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

public sealed class LeaseRenewalService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly ForgeDbContext _db;
    private readonly JobClaimOptions _options;

    public LeaseRenewalService(
        ForgeDbContext db,
        IOptions<JobClaimOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<RenewLeaseResult> RenewAsync(
        RenewLeaseRequest request,
        CancellationToken cancellationToken)
    {
        var workerId = request.WorkerInstanceId?.Trim();

        if (request.RenewRequestId == Guid.Empty ||
            request.JobId == Guid.Empty ||
            request.AttemptId == Guid.Empty ||
            string.IsNullOrWhiteSpace(workerId) ||
            workerId.Length > 200 ||
            request.FencingToken <= 0)
        {
            return Result(
                400,
                new RenewLeaseResponse(
                    request.RenewRequestId,
                    request.JobId,
                    request.AttemptId,
                    "InvalidRenewalRequest",
                    null,
                    "NotFound",
                    null,
                    false,
                    "Request identifiers must be non-empty. WorkerInstanceId " +
                    "must contain 1 to 200 characters and FencingToken must be positive."));
        }

        var fingerprint = CreateFingerprint(
            request.JobId,
            request.AttemptId,
            workerId,
            request.FencingToken);

        await using var transaction =
            await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        // Serialize retries with the same RenewRequestId.
        await AcquireRequestLockAsync(
            request.RenewRequestId,
            cancellationToken);

        var existing = await _db.LeaseRenewalRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.RenewRequestId == request.RenewRequestId,
                cancellationToken);

        if (existing is not null)
        {
            var current = await LoadCurrentClaimStateAsync(
                existing.JobId,
                existing.AttemptId,
                existing.WorkerInstanceId,
                existing.FencingToken,
                cancellationToken);

            var sameRequest = string.Equals(
                existing.RequestFingerprint,
                fingerprint,
                StringComparison.Ordinal);

            if (!sameRequest)
            {
                var conflict = new RenewLeaseResponse(
                    existing.RenewRequestId,
                    existing.JobId,
                    existing.AttemptId,
                    "IdempotencyKeyConflict",
                    existing.Outcome.ToString(),
                    current.Status,
                    existing.LeaseExpiresAt,
                    false,
                    "RenewRequestId was already used with different parameters.");

                await transaction.CommitAsync(cancellationToken);
                return Result(409, conflict);
            }

            // Keep the originally recorded outcome and expiry.
            // The current claim status is evaluated separately.
            var replay = new RenewLeaseResponse(
                existing.RenewRequestId,
                existing.JobId,
                existing.AttemptId,
                existing.Outcome.ToString(),
                null,
                current.Status,
                existing.LeaseExpiresAt,
                true);

            var replayStatus = GetHttpStatus(existing.Outcome);

            if (existing.Outcome == LeaseRenewalOutcome.Renewed &&
                current.Status != "Valid")
            {
                // The original renewal succeeded, but that historical
                // success does not prove the lease is valid now.
                replayStatus = 409;
            }

            await transaction.CommitAsync(cancellationToken);
            return Result(replayStatus, replay);
        }

        var state = await LoadCurrentClaimStateAsync(
            request.JobId,
            request.AttemptId,
            workerId,
            request.FencingToken,
            cancellationToken);

        if (state.Status != "Valid")
        {
            var outcome = OutcomeFromCurrentStatus(state.Status);

            var rejected = CreateRecord(
                request,
                workerId,
                fingerprint,
                outcome,
                state.DatabaseTime);

            var response = new RenewLeaseResponse(
                request.RenewRequestId,
                request.JobId,
                request.AttemptId,
                outcome.ToString(),
                null,
                state.Status,
                null,
                false);

            return await SaveDecisionAsync(
                rejected,
                response,
                GetHttpStatus(outcome),
                cancellationToken);
        }

        // The UPDATE itself rechecks lease validity using PostgreSQL's
        // clock. A lease that expires after the initial read cannot be revived.
        var newExpiry = await TryExtendLeaseAtomicallyAsync(
            request.JobId,
            request.AttemptId,
            workerId,
            request.FencingToken,
            cancellationToken);

        if (newExpiry is null)
        {
            // The lease expired between the read and the conditional update.
            var rejected = CreateRecord(
                request,
                workerId,
                fingerprint,
                LeaseRenewalOutcome.LeaseExpiredOrLost,
                await GetDatabaseTimeAsync(cancellationToken));

            var response = new RenewLeaseResponse(
                request.RenewRequestId,
                request.JobId,
                request.AttemptId,
                LeaseRenewalOutcome.LeaseExpiredOrLost.ToString(),
                null,
                "Expired",
                null,
                false);

            return await SaveDecisionAsync(
                rejected,
                response,
                409,
                cancellationToken);
        }

        var decidedAt = await GetDatabaseTimeAsync(cancellationToken);

        var accepted = CreateRecord(
            request,
            workerId,
            fingerprint,
            LeaseRenewalOutcome.Renewed,
            decidedAt,
            newExpiry);

        var acceptedResponse = new RenewLeaseResponse(
            request.RenewRequestId,
            request.JobId,
            request.AttemptId,
            LeaseRenewalOutcome.Renewed.ToString(),
            null,
            "Valid",
            newExpiry,
            false);

        return await SaveDecisionAsync(
            accepted,
            acceptedResponse,
            200,
            cancellationToken);
    }

    private async Task<CurrentClaimState> LoadCurrentClaimStateAsync(
        Guid jobId,
        Guid attemptId,
        string workerId,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        // All claim/renewal/completion/recovery transitions must lock Job first.
        var job = await _db.Jobs
            .FromSqlInterpolated(
                $"SELECT * FROM jobs WHERE job_id = {jobId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (job is null)
        {
            return new CurrentClaimState(
                "NotFound",
                await GetDatabaseTimeAsync(cancellationToken));
        }

        var attempt = await _db.JobAttempts
            .FromSqlInterpolated(
                $"SELECT * FROM job_attempts WHERE attempt_id = {attemptId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        var now = await GetDatabaseTimeAsync(cancellationToken);

        if (attempt is null || attempt.JobId != jobId)
        {
            return new CurrentClaimState("NotFound", now);
        }

        var ownerMatches =
            attempt.WorkerInstanceId == workerId &&
            attempt.FencingToken == fencingToken &&
            job.LastFencingToken == fencingToken;

        if (!ownerMatches || attempt.Status != JobAttemptStatus.Running)
        {
            return new CurrentClaimState("Superseded", now);
        }

        if (job.Status == JobStatus.CancelRequested)
        {
            return new CurrentClaimState("CancellationPending", now);
        }

        if (job.Status != JobStatus.Running)
        {
            return new CurrentClaimState("Superseded", now);
        }

        if (now >= attempt.LeaseExpiresAt)
        {
            return new CurrentClaimState("Expired", now);
        }

        return new CurrentClaimState("Valid", now);
    }

    private async Task<DateTimeOffset?> TryExtendLeaseAtomicallyAsync(
        Guid jobId,
        Guid attemptId,
        string workerId,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();

        command.Transaction = _db.Database.CurrentTransaction!
            .GetDbTransaction();

        command.CommandText =
            """
            UPDATE job_attempts
            SET lease_expires_at =
                clock_timestamp()
                + (@lease_seconds::double precision * INTERVAL '1 second')
            WHERE attempt_id = @attempt_id
              AND job_id = @job_id
              AND worker_instance_id = @worker_id
              AND fencing_token = @fencing_token
              AND status = 'Running'
              AND lease_expires_at > clock_timestamp()
              AND EXISTS (
                  SELECT 1
                  FROM jobs
                  WHERE jobs.job_id = @job_id
                    AND jobs.status = 'Running'
                    AND jobs.last_fencing_token = @fencing_token
              )
            RETURNING lease_expires_at
            """;

        AddParameter(command, "lease_seconds", _options.LeaseDurationSeconds);
        AddParameter(command, "attempt_id", attemptId);
        AddParameter(command, "job_id", jobId);
        AddParameter(command, "worker_id", workerId);
        AddParameter(command, "fencing_token", fencingToken);

        var value = await command.ExecuteScalarAsync(cancellationToken);

        if (value is null || value is DBNull)
        {
            return null;
        }

        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(
                "PostgreSQL returned an unexpected lease expiration type.")
        };
    }

    private async Task AcquireRequestLockAsync(
        Guid renewRequestId,
        CancellationToken cancellationToken)
    {
        await using var command =
            _db.Database.GetDbConnection().CreateCommand();

        command.Transaction = _db.Database.CurrentTransaction!
            .GetDbTransaction();

        command.CommandText =
            "SELECT pg_advisory_xact_lock(" +
            "hashtextextended(@renew_request_id, 0))";

        AddParameter(
            command,
            "renew_request_id",
            renewRequestId.ToString("D"));

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

    private async Task<RenewLeaseResult> SaveDecisionAsync(
        LeaseRenewalRequest record,
        RenewLeaseResponse response,
        int statusCode,
        CancellationToken cancellationToken)
    {
        _db.LeaseRenewalRequests.Add(record);

        await _db.SaveChangesAsync(cancellationToken);
        await _db.Database.CurrentTransaction!
            .CommitAsync(cancellationToken);

        return Result(statusCode, response);
    }

    private static LeaseRenewalRequest CreateRecord(
        RenewLeaseRequest request,
        string workerId,
        string fingerprint,
        LeaseRenewalOutcome outcome,
        DateTimeOffset decidedAt,
        DateTimeOffset? leaseExpiresAt = null)
    {
        return new LeaseRenewalRequest
        {
            RenewRequestId = request.RenewRequestId,
            JobId = request.JobId,
            AttemptId = request.AttemptId,
            WorkerInstanceId = workerId,
            FencingToken = request.FencingToken,
            RequestFingerprint = fingerprint,
            Outcome = outcome,
            LeaseExpiresAt = leaseExpiresAt,
            DecidedAt = decidedAt
        };
    }

    private static LeaseRenewalOutcome OutcomeFromCurrentStatus(
        string status)
    {
        return status switch
        {
            "NotFound" => LeaseRenewalOutcome.NotFound,
            "Expired" => LeaseRenewalOutcome.LeaseExpiredOrLost,
            "CancellationPending" =>
                LeaseRenewalOutcome.CancellationPending,
            _ => LeaseRenewalOutcome.NotOwnerOrSuperseded
        };
    }

    private static int GetHttpStatus(LeaseRenewalOutcome outcome)
    {
        return outcome switch
        {
            LeaseRenewalOutcome.Renewed => 200,
            LeaseRenewalOutcome.NotFound => 404,
            _ => 409
        };
    }

    private static string CreateFingerprint(
        Guid jobId,
        Guid attemptId,
        string workerId,
        long fencingToken)
    {
        var canonical = string.Join(
            "\n",
            jobId.ToString("D"),
            attemptId.ToString("D"),
            workerId,
            fencingToken.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static void AddParameter(
        System.Data.Common.DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static RenewLeaseResult Result(
        int statusCode,
        RenewLeaseResponse response)
    {
        return new RenewLeaseResult(
            statusCode,
            JsonSerializer.Serialize(response, JsonOptions));
    }

    private sealed record CurrentClaimState(
        string Status,
        DateTimeOffset DatabaseTime);
}
