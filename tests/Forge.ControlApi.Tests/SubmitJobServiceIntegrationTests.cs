using Forge.ControlApi.Contracts.Messaging;
using System.Text.Json;
using Forge.ControlApi.Contracts.Jobs;
using Forge.ControlApi.Persistence;
using Forge.ControlApi.Persistence.Entities;
using Forge.ControlApi.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Forge.ControlApi.Tests;

public class SubmitJobServiceIntegrationTests
{
    private const string ClientScopeId = "integration-client-a";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SubmitAsync_ValidRequest_CreatesJobSubmissionAndOutbox()
    {
        await ResetDatabaseAsync();

        var request = CreateRequest();

        await using (var db = CreateDbContext())
        {
            var result = await new SubmitJobService(new PostgresJobSubmissionStore(db)).SubmitAsync(
                ClientScopeId,
                request,
                CancellationToken.None);

            Assert.Equal(202, result.StatusCode);

            var receipt =
                JsonSerializer.Deserialize<JobSubmissionReceipt>(
                    result.ResponseBodyJson,
                    JsonOptions)!;

            Assert.Equal(request.SubmissionRequestId,
                receipt.SubmissionRequestId);

            Assert.Equal("Accepted", receipt.Outcome);
            Assert.NotEqual(Guid.Empty, receipt.JobId);
        }

        await using var verify = CreateDbContext();

        var job = await verify.Jobs.SingleAsync();
        var submission =
            await verify.SubmissionRequests.SingleAsync();
        var outbox = await verify.OutboxMessages.SingleAsync();

        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal("log-analysis", job.JobType);
        Assert.Equal(submission.JobId, job.JobId);
        Assert.Equal(job.JobId, outbox.JobId);
        Assert.Equal(
            OutboxMessageTypes.JobAvailableV1,
            outbox.MessageType);
        Assert.Null(outbox.PublishedAt);
        Assert.Equal(0, outbox.AttemptCount);
        Assert.Equal("Accepted", submission.Outcome.ToString());
    }

    [Fact]
    public async Task SubmitAsync_SameRequest_ReturnsOriginalReceipt()
    {
        await ResetDatabaseAsync();

        var request = CreateRequest();
        SubmitJobResult first;

        await using (var db = CreateDbContext())
        {
            first = await new SubmitJobService(new PostgresJobSubmissionStore(db)).SubmitAsync(
                ClientScopeId,
                request,
                CancellationToken.None);
        }

        SubmitJobResult second;

        await using (var db = CreateDbContext())
        {
            second = await new SubmitJobService(new PostgresJobSubmissionStore(db)).SubmitAsync(
                ClientScopeId,
                request,
                CancellationToken.None);
        }

       Assert.Equal(202, second.StatusCode);

var firstReceipt =
    JsonSerializer.Deserialize<JobSubmissionReceipt>(
        first.ResponseBodyJson,
        JsonOptions)!;

var secondReceipt =
    JsonSerializer.Deserialize<JobSubmissionReceipt>(
        second.ResponseBodyJson,
        JsonOptions)!;

Assert.Equal(firstReceipt.SubmissionRequestId,
    secondReceipt.SubmissionRequestId);

Assert.Equal(firstReceipt.JobId, secondReceipt.JobId);
Assert.Equal(firstReceipt.Outcome, secondReceipt.Outcome);
Assert.Equal(firstReceipt.AcceptedAt, secondReceipt.AcceptedAt);

        await using var verify = CreateDbContext();

        Assert.Equal(1, await verify.Jobs.CountAsync());
        Assert.Equal(1, await verify.SubmissionRequests.CountAsync());
        Assert.Equal(1, await verify.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task SubmitAsync_SameKeyDifferentPayload_ReturnsConflict()
    {
        await ResetDatabaseAsync();

        var request = CreateRequest(text: "first payload");

        await using (var db = CreateDbContext())
        {
            var first = await new SubmitJobService(new PostgresJobSubmissionStore(db)).SubmitAsync(
                ClientScopeId,
                request,
                CancellationToken.None);

            Assert.Equal(202, first.StatusCode);
        }

        var conflictingRequest = CreateRequest(
            requestId: request.SubmissionRequestId,
            text: "different payload");

        await using (var db = CreateDbContext())
        {
            var conflict = await new SubmitJobService(new PostgresJobSubmissionStore(db)).SubmitAsync(
                ClientScopeId,
                conflictingRequest,
                CancellationToken.None);

            Assert.Equal(409, conflict.StatusCode);

            var rejection =
                JsonSerializer.Deserialize<JobSubmissionRejection>(
                    conflict.ResponseBodyJson,
                    JsonOptions)!;

            Assert.Equal(
                "IdempotencyKeyConflict",
                rejection.ErrorCode);
        }

        await using var verify = CreateDbContext();

        Assert.Equal(1, await verify.Jobs.CountAsync());
        Assert.Equal(1, await verify.SubmissionRequests.CountAsync());
        Assert.Equal(1, await verify.OutboxMessages.CountAsync());
    }
[Fact]
public async Task SubmitAsync_OutboxInsertFails_RollsBackAllRecords()
{
    await ResetDatabaseAsync();

    var request = CreateRequest();

    await using (var setup = CreateDbContext())
    {
        await setup.Database.ExecuteSqlRawAsync(
            """
            DROP TRIGGER IF EXISTS
                trg_forge_test_fail_outbox_insert
                ON outbox_messages;

            DROP FUNCTION IF EXISTS
                public.forge_test_fail_outbox_insert();
            """);

        await setup.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION public.forge_test_fail_outbox_insert()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                RAISE EXCEPTION 'Simulated outbox insert failure'
                    USING ERRCODE = 'P0001';
            END;
            $function$;
            """);

        await setup.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER trg_forge_test_fail_outbox_insert
            BEFORE INSERT ON outbox_messages
            FOR EACH ROW
            EXECUTE FUNCTION public.forge_test_fail_outbox_insert();
            """);
    }

    try
    {
        await using var db = CreateDbContext();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => new SubmitJobService(new PostgresJobSubmissionStore(db)).SubmitAsync(
                ClientScopeId,
                request,
                CancellationToken.None));
    }
    finally
    {
        await using var cleanup = CreateDbContext();

        await cleanup.Database.ExecuteSqlRawAsync(
            """
            DROP TRIGGER IF EXISTS
                trg_forge_test_fail_outbox_insert
                ON outbox_messages;
            """);

        await cleanup.Database.ExecuteSqlRawAsync(
            """
            DROP FUNCTION IF EXISTS
                public.forge_test_fail_outbox_insert();
            """);
    }

    await using var verify = CreateDbContext();

    Assert.Equal(0, await verify.Jobs.CountAsync());
    Assert.Equal(0, await verify.SubmissionRequests.CountAsync());
    Assert.Equal(0, await verify.OutboxMessages.CountAsync());
}
    [Fact]
    public async Task SubmitAsync_InvalidRequest_ReplaysRecordedRejection()
    {
        await ResetDatabaseAsync();

        var request = CreateRequest(jobType: "unsupported-job");

        SubmitJobResult first;

        await using (var db = CreateDbContext())
        {
            first = await new SubmitJobService(new PostgresJobSubmissionStore(db)).SubmitAsync(
                ClientScopeId,
                request,
                CancellationToken.None);
        }

        SubmitJobResult second;

        await using (var db = CreateDbContext())
        {
            second = await new SubmitJobService(new PostgresJobSubmissionStore(db)).SubmitAsync(
                ClientScopeId,
                request,
                CancellationToken.None);
        }

      Assert.Equal(422, first.StatusCode);
Assert.Equal(first.StatusCode, second.StatusCode);

var firstRejection =
    JsonSerializer.Deserialize<JobSubmissionRejection>(
        first.ResponseBodyJson,
        JsonOptions)!;

var secondRejection =
    JsonSerializer.Deserialize<JobSubmissionRejection>(
        second.ResponseBodyJson,
        JsonOptions)!;

Assert.Equal(firstRejection.SubmissionRequestId,
    secondRejection.SubmissionRequestId);

Assert.Equal(firstRejection.Outcome, secondRejection.Outcome);
Assert.Equal(firstRejection.ErrorCode, secondRejection.ErrorCode);
Assert.Equal(firstRejection.Message, secondRejection.Message);

        await using var verify = CreateDbContext();

        Assert.Equal(0, await verify.Jobs.CountAsync());
        Assert.Equal(1, await verify.SubmissionRequests.CountAsync());
        Assert.Equal(0, await verify.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task SubmitAsync_ConcurrentDuplicateRequests_CreatesOneJob()
    {
        await ResetDatabaseAsync();

        var request = CreateRequest();

        async Task<SubmitJobResult> SubmitOnceAsync()
        {
            await using var db = CreateDbContext();

            return await new SubmitJobService(new PostgresJobSubmissionStore(db)).SubmitAsync(
                ClientScopeId,
                request,
                CancellationToken.None);
        }

        var results = await Task.WhenAll(
            SubmitOnceAsync(),
            SubmitOnceAsync());

        Assert.All(results, result => Assert.Equal(202, result.StatusCode));

      var firstReceipt =
    JsonSerializer.Deserialize<JobSubmissionReceipt>(
        results[0].ResponseBodyJson,
        JsonOptions)!;

var secondReceipt =
    JsonSerializer.Deserialize<JobSubmissionReceipt>(
        results[1].ResponseBodyJson,
        JsonOptions)!;

Assert.Equal(firstReceipt.JobId, secondReceipt.JobId);
Assert.Equal(firstReceipt.SubmissionRequestId,
    secondReceipt.SubmissionRequestId);

Assert.Equal(firstReceipt.AcceptedAt, secondReceipt.AcceptedAt);
Assert.Equal(firstReceipt.Outcome, secondReceipt.Outcome);

        await using var verify = CreateDbContext();

        Assert.Equal(1, await verify.Jobs.CountAsync());
        Assert.Equal(1, await verify.SubmissionRequests.CountAsync());
        Assert.Equal(1, await verify.OutboxMessages.CountAsync());
    }


    [Fact]
    public async Task ClaimAsync_EligibleJob_GrantsAndPersistsAttempt()
    {
        await ResetDatabaseAsync();

        var jobId = await CreateQueuedJobAsync();
        var request = new ClaimJobRequest
        {
            JobId = jobId,
            ClaimRequestId = Guid.NewGuid(),
            WorkerInstanceId = "worker-claim-a"
        };

        ClaimJobResult result;

        await using (var db = CreateDbContext())
        {
            result = await CreateClaimJobService(db).ClaimAsync(
                request,
                CancellationToken.None);
        }

        Assert.Equal(200, result.StatusCode);

        var response = DeserializeClaimResponse(result);
        Assert.Equal("Granted", response.Outcome);
        Assert.Equal("Valid", response.CurrentClaimStatus);
        Assert.NotNull(response.AttemptId);
        Assert.Equal(1L, response.FencingToken);
        Assert.NotNull(response.LeaseExpiresAt);
        Assert.NotNull(response.ExecutionDescriptor);
        Assert.Equal("log-analysis", response.ExecutionDescriptor.JobType);

        await using var verify = CreateDbContext();

        var job = await verify.Jobs.SingleAsync();
        var attempt = await verify.JobAttempts.SingleAsync();
        var claim = await verify.ClaimRequests.SingleAsync();

        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(1L, job.LastFencingToken);
        Assert.Equal(response.AttemptId, attempt.AttemptId);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(JobAttemptStatus.Running, attempt.Status);
        Assert.Equal(response.FencingToken, attempt.FencingToken);
        Assert.Equal(request.ClaimRequestId, claim.ClaimRequestId);
        Assert.Equal(ClaimRequestOutcome.Granted, claim.Outcome);
    }

    [Fact]
    public async Task ClaimAsync_SameRequest_ReplaysOriginalClaimWithoutNewAttempt()
    {
        await ResetDatabaseAsync();

        var jobId = await CreateQueuedJobAsync();
        var request = new ClaimJobRequest
        {
            JobId = jobId,
            ClaimRequestId = Guid.NewGuid(),
            WorkerInstanceId = "worker-claim-replay"
        };

        ClaimJobResult first;
        await using (var db = CreateDbContext())
        {
            first = await CreateClaimJobService(db).ClaimAsync(
                request,
                CancellationToken.None);
        }

        ClaimJobResult second;
        await using (var db = CreateDbContext())
        {
            second = await CreateClaimJobService(db).ClaimAsync(
                request,
                CancellationToken.None);
        }

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);

        var firstResponse = DeserializeClaimResponse(first);
        var secondResponse = DeserializeClaimResponse(second);

        Assert.Equal("Granted", secondResponse.Outcome);
        Assert.Equal("Valid", secondResponse.CurrentClaimStatus);
        Assert.Equal(firstResponse.AttemptId, secondResponse.AttemptId);
        Assert.Equal(firstResponse.FencingToken, secondResponse.FencingToken);
        Assert.Equal(firstResponse.LeaseExpiresAt, secondResponse.LeaseExpiresAt);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.JobAttempts.CountAsync());
        Assert.Equal(1, await verify.ClaimRequests.CountAsync());
    }

    [Fact]
    public async Task ClaimAsync_SameRequestIdDifferentWorker_ReturnsConflict()
    {
        await ResetDatabaseAsync();

        var jobId = await CreateQueuedJobAsync();
        var requestId = Guid.NewGuid();

        var firstRequest = new ClaimJobRequest
        {
            JobId = jobId,
            ClaimRequestId = requestId,
            WorkerInstanceId = "worker-original"
        };

        var conflictingRequest = new ClaimJobRequest
        {
            JobId = jobId,
            ClaimRequestId = requestId,
            WorkerInstanceId = "worker-different"
        };

        await using (var db = CreateDbContext())
        {
            var first = await CreateClaimJobService(db).ClaimAsync(
                firstRequest,
                CancellationToken.None);

            Assert.Equal(200, first.StatusCode);
        }

        await using (var db = CreateDbContext())
        {
            var conflict = await CreateClaimJobService(db).ClaimAsync(
                conflictingRequest,
                CancellationToken.None);

            Assert.Equal(409, conflict.StatusCode);
            Assert.Equal(
                "IdempotencyKeyConflict",
                DeserializeClaimResponse(conflict).Outcome);
        }

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.JobAttempts.CountAsync());
        Assert.Equal(1, await verify.ClaimRequests.CountAsync());
    }

    [Fact]
    public async Task ClaimAsync_UnknownJob_RecordsAndReplaysNotFound()
    {
        await ResetDatabaseAsync();

        var request = new ClaimJobRequest
        {
            JobId = Guid.NewGuid(),
            ClaimRequestId = Guid.NewGuid(),
            WorkerInstanceId = "worker-unknown-job"
        };

        ClaimJobResult first;
        await using (var db = CreateDbContext())
        {
            first = await CreateClaimJobService(db).ClaimAsync(
                request,
                CancellationToken.None);
        }

        ClaimJobResult second;
        await using (var db = CreateDbContext())
        {
            second = await CreateClaimJobService(db).ClaimAsync(
                request,
                CancellationToken.None);
        }

        Assert.Equal(404, first.StatusCode);
        Assert.Equal(404, second.StatusCode);
        Assert.Equal("NotFound", DeserializeClaimResponse(second).Outcome);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.ClaimRequests.CountAsync());
        Assert.Equal(0, await verify.JobAttempts.CountAsync());
    }

    [Fact]
    public async Task ClaimAsync_ConcurrentWorkersForSameJob_OnlyOneIsGranted()
    {
        await ResetDatabaseAsync();

        var jobId = await CreateQueuedJobAsync();

        async Task<(ClaimJobResult Result, ClaimJobResponse Response)> ClaimOnce(
            string workerId)
        {
            var request = new ClaimJobRequest
            {
                JobId = jobId,
                ClaimRequestId = Guid.NewGuid(),
                WorkerInstanceId = workerId
            };

            await using var db = CreateDbContext();

            var result = await CreateClaimJobService(db).ClaimAsync(
                request,
                CancellationToken.None);

            return (result, DeserializeClaimResponse(result));
        }

        var results = await Task.WhenAll(
            ClaimOnce("worker-race-a"),
            ClaimOnce("worker-race-b"));

       var granted = Assert.Single(
    results,
    x => x.Result.StatusCode == 200);

var rejected = Assert.Single(
    results,
    x => x.Result.StatusCode == 409);

        Assert.Equal("Granted", granted.Response.Outcome);
        Assert.Equal("AlreadyClaimed", rejected.Response.Outcome);

        await using var verify = CreateDbContext();

        var job = await verify.Jobs.SingleAsync();
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(1L, job.LastFencingToken);
        Assert.Equal(1, await verify.JobAttempts.CountAsync());
        Assert.Equal(2, await verify.ClaimRequests.CountAsync());
    }

    [Fact]
    public async Task ClaimAsync_ReplayAfterLeaseExpiry_DoesNotRestoreOwnership()
    {
        await ResetDatabaseAsync();

        var jobId = await CreateQueuedJobAsync();
        var request = new ClaimJobRequest
        {
            JobId = jobId,
            ClaimRequestId = Guid.NewGuid(),
            WorkerInstanceId = "worker-expired-claim"
        };

        ClaimJobResult first;
        await using (var db = CreateDbContext())
        {
            first = await CreateClaimJobService(db).ClaimAsync(
                request,
                CancellationToken.None);
        }

        Assert.Equal(200, first.StatusCode);
        var original = DeserializeClaimResponse(first);
        var attemptId = Assert.IsType<Guid>(original.AttemptId);

        // Move the attempt's lease into the past while respecting the
        // database constraint that lease_expires_at must follow claimed_at.
        await using (var db = CreateDbContext())
        {
            var updated = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE job_attempts
                SET claimed_at = clock_timestamp() - INTERVAL '5 minutes',
                    lease_expires_at = clock_timestamp() - INTERVAL '1 second'
                WHERE attempt_id = {attemptId}
                """);

            Assert.Equal(1, updated);
        }

        ClaimJobResult replay;
        await using (var db = CreateDbContext())
        {
            replay = await CreateClaimJobService(db).ClaimAsync(
                request,
                CancellationToken.None);
        }

        Assert.Equal(409, replay.StatusCode);

        var response = DeserializeClaimResponse(replay);
        Assert.Equal("ExpiredOrLost", response.Outcome);
        Assert.Equal("Granted", response.OriginalOutcome);
        Assert.Equal("Expired", response.CurrentClaimStatus);
        Assert.Equal(original.AttemptId, response.AttemptId);
        Assert.Equal(original.FencingToken, response.FencingToken);

        // The replay returns the original recorded expiry and does not extend it.
        Assert.Equal(original.LeaseExpiresAt, response.LeaseExpiresAt);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.JobAttempts.CountAsync());
        Assert.Equal(1, await verify.ClaimRequests.CountAsync());
    }

    private static ClaimJobService CreateClaimJobService(ForgeDbContext db)
    {
        return new ClaimJobService(
            db,
            Options.Create(new JobClaimOptions
            {
                LeaseDurationSeconds = 60
            }));
    }

    private static ClaimJobResponse DeserializeClaimResponse(
        ClaimJobResult result)
    {
        return JsonSerializer.Deserialize<ClaimJobResponse>(
            result.ResponseBodyJson,
            JsonOptions)!;
    }

    private static async Task<Guid> CreateQueuedJobAsync()
    {
        var jobId = Guid.NewGuid();

        await using var db = CreateDbContext();

        db.Jobs.Add(new Job
        {
            JobId = jobId,
            JobType = "log-analysis",
            Status = JobStatus.Queued,
            InputPayloadJson = """{"text":"claim integration test"}""",
            CreatedAt = DateTimeOffset.UtcNow,
            NextEligibleAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastFencingToken = 0
        });

        await db.SaveChangesAsync();
        return jobId;
    }


    [Fact]
    public async Task RenewLeaseAsync_ValidClaim_ExtendsLeaseAndPersistsOutcome()
    {
        await ResetDatabaseAsync();

        const string workerId = "worker-renew-valid";
        var (jobId, claim) = await CreateClaimedJobAsync(workerId);
        var request = CreateRenewalRequest(jobId, claim, workerId);

        RenewLeaseResult result;
        await using (var db = CreateDbContext())
        {
            result = await CreateLeaseRenewalService(db).RenewAsync(
                request, CancellationToken.None);
        }

        Assert.Equal(200, result.StatusCode);

        var response = DeserializeRenewResponse(result);
        Assert.Equal("Renewed", response.Outcome);
        Assert.Equal("Valid", response.CurrentClaimStatus);
        Assert.False(response.IsReplay);
        Assert.NotNull(response.LeaseExpiresAt);
        Assert.True(response.LeaseExpiresAt.Value > claim.LeaseExpiresAt!.Value);

        await using var verify = CreateDbContext();
        var attempt = await verify.JobAttempts.SingleAsync();
        var renewal = await verify.LeaseRenewalRequests.SingleAsync();

        Assert.Equal(response.LeaseExpiresAt, attempt.LeaseExpiresAt);
        Assert.Equal(request.RenewRequestId, renewal.RenewRequestId);
        Assert.Equal(LeaseRenewalOutcome.Renewed, renewal.Outcome);
        Assert.Equal(response.LeaseExpiresAt, renewal.LeaseExpiresAt);
    }

    [Fact]
    public async Task RenewLeaseAsync_SameRequest_ReplaysWithoutExtendingAgain()
    {
        await ResetDatabaseAsync();

        const string workerId = "worker-renew-replay";
        var (jobId, claim) = await CreateClaimedJobAsync(workerId);
        var request = CreateRenewalRequest(jobId, claim, workerId);

        RenewLeaseResult first;
        await using (var db = CreateDbContext())
        {
            first = await CreateLeaseRenewalService(db).RenewAsync(
                request, CancellationToken.None);
        }

        Assert.Equal(200, first.StatusCode);
        var firstResponse = DeserializeRenewResponse(first);

        RenewLeaseResult second;
        await using (var db = CreateDbContext())
        {
            second = await CreateLeaseRenewalService(db).RenewAsync(
                request, CancellationToken.None);
        }

        Assert.Equal(200, second.StatusCode);
        var secondResponse = DeserializeRenewResponse(second);

        Assert.Equal("Renewed", secondResponse.Outcome);
        Assert.True(secondResponse.IsReplay);
        Assert.Equal("Valid", secondResponse.CurrentClaimStatus);
        Assert.Equal(firstResponse.LeaseExpiresAt, secondResponse.LeaseExpiresAt);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.LeaseRenewalRequests.CountAsync());
    }

    [Fact]
    public async Task RenewLeaseAsync_SameRequestIdDifferentWorker_ReturnsConflict()
    {
        await ResetDatabaseAsync();

        const string workerId = "worker-renew-original";
        var (jobId, claim) = await CreateClaimedJobAsync(workerId);
        var request = CreateRenewalRequest(jobId, claim, workerId);

        await using (var db = CreateDbContext())
        {
            var result = await CreateLeaseRenewalService(db).RenewAsync(
                request, CancellationToken.None);
            Assert.Equal(200, result.StatusCode);
        }

        var conflictingRequest = CreateRenewalRequest(
            jobId, claim, "worker-renew-different", request.RenewRequestId);

        await using (var db = CreateDbContext())
        {
            var result = await CreateLeaseRenewalService(db).RenewAsync(
                conflictingRequest, CancellationToken.None);

            Assert.Equal(409, result.StatusCode);
            var response = DeserializeRenewResponse(result);
            Assert.Equal("IdempotencyKeyConflict", response.Outcome);
            Assert.Equal("Renewed", response.OriginalOutcome);
        }

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.LeaseRenewalRequests.CountAsync());
    }

    [Fact]
    public async Task RenewLeaseAsync_ExpiredLease_IsRejectedAndReplayRemainsExpired()
    {
        await ResetDatabaseAsync();

        const string workerId = "worker-renew-expired";
        var (jobId, claim) = await CreateClaimedJobAsync(workerId);
        var attemptId = claim.AttemptId!.Value;
        var request = CreateRenewalRequest(jobId, claim, workerId);

        await using (var db = CreateDbContext())
        {
            var updated = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE job_attempts
                SET claimed_at = clock_timestamp() - INTERVAL '5 minutes',
                    lease_expires_at = clock_timestamp() - INTERVAL '1 second'
                WHERE attempt_id = {attemptId}
                """);

            Assert.Equal(1, updated);
        }

        RenewLeaseResult first;
        await using (var db = CreateDbContext())
        {
            first = await CreateLeaseRenewalService(db).RenewAsync(
                request, CancellationToken.None);
        }

        Assert.Equal(409, first.StatusCode);
        var firstResponse = DeserializeRenewResponse(first);
        Assert.Equal("LeaseExpiredOrLost", firstResponse.Outcome);
        Assert.Equal("Expired", firstResponse.CurrentClaimStatus);
        Assert.Null(firstResponse.LeaseExpiresAt);

        RenewLeaseResult replay;
        await using (var db = CreateDbContext())
        {
            replay = await CreateLeaseRenewalService(db).RenewAsync(
                request, CancellationToken.None);
        }

        Assert.Equal(409, replay.StatusCode);
        var replayResponse = DeserializeRenewResponse(replay);
        Assert.True(replayResponse.IsReplay);
        Assert.Equal("LeaseExpiredOrLost", replayResponse.Outcome);
        Assert.Equal("Expired", replayResponse.CurrentClaimStatus);
        Assert.Null(replayResponse.LeaseExpiresAt);

        await using var verify = CreateDbContext();
        var renewal = await verify.LeaseRenewalRequests.SingleAsync();
        Assert.Equal(LeaseRenewalOutcome.LeaseExpiredOrLost, renewal.Outcome);
        Assert.Null(renewal.LeaseExpiresAt);
    }

    [Fact]
    public async Task RenewLeaseAsync_WrongFencingToken_IsRejected()
    {
        await ResetDatabaseAsync();

        const string workerId = "worker-renew-stale-token";
        var (jobId, claim) = await CreateClaimedJobAsync(workerId);

        var request = CreateRenewalRequest(
            jobId,
            claim,
            workerId,
            fencingToken: checked(claim.FencingToken!.Value + 1));

        RenewLeaseResult result;
        await using (var db = CreateDbContext())
        {
            result = await CreateLeaseRenewalService(db).RenewAsync(
                request, CancellationToken.None);
        }

        Assert.Equal(409, result.StatusCode);
        var response = DeserializeRenewResponse(result);
        Assert.Equal("NotOwnerOrSuperseded", response.Outcome);
        Assert.Equal("Superseded", response.CurrentClaimStatus);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.JobAttempts.CountAsync());
        Assert.Equal(1, await verify.LeaseRenewalRequests.CountAsync());

        var attempt = await verify.JobAttempts.SingleAsync();
        Assert.Equal(claim.FencingToken, attempt.FencingToken);
        Assert.Equal(claim.LeaseExpiresAt, attempt.LeaseExpiresAt);
    }

    [Fact]
    public async Task RenewLeaseAsync_UnknownJob_RecordsAndReplaysNotFound()
    {
        await ResetDatabaseAsync();

        var request = new RenewLeaseRequest
        {
            RenewRequestId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            WorkerInstanceId = "worker-renew-unknown-job",
            FencingToken = 1
        };

        RenewLeaseResult first;
        await using (var db = CreateDbContext())
        {
            first = await CreateLeaseRenewalService(db).RenewAsync(
                request, CancellationToken.None);
        }

        Assert.Equal(404, first.StatusCode);
        Assert.Equal("NotFound", DeserializeRenewResponse(first).Outcome);

        RenewLeaseResult replay;
        await using (var db = CreateDbContext())
        {
            replay = await CreateLeaseRenewalService(db).RenewAsync(
                request, CancellationToken.None);
        }

        Assert.Equal(404, replay.StatusCode);
        var response = DeserializeRenewResponse(replay);
        Assert.Equal("NotFound", response.Outcome);
        Assert.Equal("NotFound", response.CurrentClaimStatus);
        Assert.True(response.IsReplay);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.LeaseRenewalRequests.CountAsync());
    }


    [Fact]
    public async Task RecoverExpiredClaimsAsync_ExpiredLease_RequeuesJobAndWritesOutbox()
    {
        await ResetDatabaseAsync();

        const string workerId = "worker-recovery-requeue";
        var (jobId, claim) = await CreateClaimedJobAsync(workerId);
        var attemptId = claim.AttemptId!.Value;

        await using (var db = CreateDbContext())
        {
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE job_attempts
                SET claimed_at = clock_timestamp() - INTERVAL '5 minutes',
                    lease_expires_at = clock_timestamp() - INTERVAL '1 second'
                WHERE attempt_id = {attemptId}
                """);

            Assert.Equal(1, affected);
        }

        int recovered;

        await using (var db = CreateDbContext())
        {
            recovered = await CreateLeaseRecoveryService(db)
                .RecoverExpiredClaimsAsync(CancellationToken.None);
        }

        Assert.Equal(1, recovered);

        await using var verify = CreateDbContext();

        var job = await verify.Jobs.SingleAsync(x => x.JobId == jobId);
        var attempt = await verify.JobAttempts.SingleAsync(
            x => x.AttemptId == attemptId);
        var outbox = await verify.OutboxMessages.SingleAsync(
            x => x.JobId == jobId);

        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(JobAttemptStatus.LeaseExpired, attempt.Status);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Equal("LEASE_EXPIRED", attempt.FailureCode);

        Assert.True(job.NextEligibleAt > attempt.CompletedAt!.Value);
        Assert.Equal(OutboxMessageTypes.JobAvailableV1, outbox.MessageType);
        Assert.Equal(job.NextEligibleAt, outbox.NextAttemptAt);
        Assert.Null(outbox.PublishedAt);

        var payload = JsonSerializer.Deserialize<JobAvailableV1Payload>(
            outbox.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(payload);
        Assert.Equal(outbox.OutboxMessageId, payload.MessageId);
        Assert.Equal(jobId, payload.JobId);
        Assert.Equal(job.JobType, payload.JobType);
    }


    [Fact]
    public async Task RecoverExpiredClaimsAsync_AttemptLimitExhausted_MarksJobFailed()
    {
        await ResetDatabaseAsync();

        var (jobId, claim) = await CreateClaimedJobAsync("worker-last-attempt");
        var attemptId = claim.AttemptId!.Value;

        await using (var db = CreateDbContext())
        {
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE job_attempts
                SET claimed_at = clock_timestamp() - INTERVAL '5 minutes',
                    lease_expires_at = clock_timestamp() - INTERVAL '1 second'
                WHERE attempt_id = {attemptId}
                """);

            Assert.Equal(1, affected);
        }

        int recovered;
        await using (var db = CreateDbContext())
        {
            recovered = await CreateLeaseRecoveryService(db, maxAttempts: 1)
                .RecoverExpiredClaimsAsync(CancellationToken.None);
        }

        Assert.Equal(1, recovered);

        await using var verify = CreateDbContext();
        var job = await verify.Jobs.SingleAsync(x => x.JobId == jobId);
        var attempt = await verify.JobAttempts.SingleAsync(
            x => x.AttemptId == attemptId);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(JobAttemptStatus.LeaseExpired, attempt.Status);
        Assert.Equal("LEASE_EXPIRED", attempt.FailureCode);

        Assert.Empty(await verify.OutboxMessages
            .Where(x => x.JobId == jobId)
            .ToListAsync());
    }


    [Fact]
    public async Task RecoverExpiredClaimsAsync_CancellationPending_FinalizesCancellation()
    {
        await ResetDatabaseAsync();

        var (jobId, claim) =
            await CreateClaimedJobAsync("worker-recovery-cancel");
        var attemptId = claim.AttemptId!.Value;

        await using (var db = CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync(x => x.JobId == jobId);
            job.Status = JobStatus.CancelRequested;
            await db.SaveChangesAsync();

            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE job_attempts
                SET claimed_at = clock_timestamp() - INTERVAL '5 minutes',
                    lease_expires_at = clock_timestamp() - INTERVAL '1 second'
                WHERE attempt_id = {attemptId}
                """);

            Assert.Equal(1, affected);
        }

        int recovered;
        await using (var db = CreateDbContext())
        {
            recovered = await CreateLeaseRecoveryService(db)
                .RecoverExpiredClaimsAsync(CancellationToken.None);
        }

        Assert.Equal(1, recovered);

        await using var verify = CreateDbContext();
        var jobAfter = await verify.Jobs.SingleAsync(x => x.JobId == jobId);
        var attempt = await verify.JobAttempts.SingleAsync(
            x => x.AttemptId == attemptId);

        Assert.Equal(JobStatus.Cancelled, jobAfter.Status);
        Assert.Equal(JobAttemptStatus.LeaseExpired, attempt.Status);
        Assert.NotNull(attempt.CompletedAt);

        Assert.Empty(await verify.OutboxMessages
            .Where(x => x.JobId == jobId)
            .ToListAsync());
    }


    [Fact]
    public async Task RecoverExpiredClaimsAsync_ConcurrentRenewal_CannotReviveExpiredLease()
    {
        await ResetDatabaseAsync();

        const string workerId = "worker-recovery-renew-race";
        var (jobId, claim) = await CreateClaimedJobAsync(workerId);
        var attemptId = claim.AttemptId!.Value;

        await using (var db = CreateDbContext())
        {
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE job_attempts
                SET claimed_at = clock_timestamp() - INTERVAL '5 minutes',
                    lease_expires_at = clock_timestamp() - INTERVAL '1 second'
                WHERE attempt_id = {attemptId}
                """);

            Assert.Equal(1, affected);
        }

        var renewRequest = CreateRenewalRequest(jobId, claim, workerId);

        async Task<int> RecoverOnceAsync()
        {
            await using var db = CreateDbContext();
            return await CreateLeaseRecoveryService(db)
                .RecoverExpiredClaimsAsync(CancellationToken.None);
        }

        async Task<RenewLeaseResult> RenewOnceAsync()
        {
            await using var db = CreateDbContext();
            return await CreateLeaseRenewalService(db).RenewAsync(
                renewRequest, CancellationToken.None);
        }

        var recoveryTask = RecoverOnceAsync();
        var renewalTask = RenewOnceAsync();

        await Task.WhenAll(recoveryTask, renewalTask);

        var renewed = await renewalTask;
        Assert.NotEqual(200, renewed.StatusCode);

        var recoveredCount = await recoveryTask;

        // Recovery may skip a row locked by renewal. A subsequent sweep
        // must process it after the competing transaction has completed.
        if (recoveredCount == 0)
        {
            recoveredCount = await RecoverOnceAsync();
        }

        Assert.Equal(1, recoveredCount);

        await using var verify = CreateDbContext();

        var job = await verify.Jobs.SingleAsync(x => x.JobId == jobId);
        var attempt = await verify.JobAttempts.SingleAsync(
            x => x.AttemptId == attemptId);
        var renewalRecord = await verify.LeaseRenewalRequests.SingleAsync();

        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(JobAttemptStatus.LeaseExpired, attempt.Status);
        Assert.Equal("LEASE_EXPIRED", attempt.FailureCode);
        Assert.NotEqual(LeaseRenewalOutcome.Renewed, renewalRecord.Outcome);

        Assert.Single(await verify.OutboxMessages
            .Where(x => x.JobId == jobId)
            .ToListAsync());
    }


    [Fact]
    public async Task RecoverExpiredClaimsAsync_RepeatedSweep_DoesNotDuplicateOutbox()
    {
        await ResetDatabaseAsync();

        var (jobId, claim) =
            await CreateClaimedJobAsync("worker-recovery-repeat");
        var attemptId = claim.AttemptId!.Value;

        await using (var db = CreateDbContext())
        {
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE job_attempts
                SET claimed_at = clock_timestamp() - INTERVAL '5 minutes',
                    lease_expires_at = clock_timestamp() - INTERVAL '1 second'
                WHERE attempt_id = {attemptId}
                """);

            Assert.Equal(1, affected);
        }

        int firstSweep;
        await using (var db = CreateDbContext())
        {
            firstSweep = await CreateLeaseRecoveryService(db)
                .RecoverExpiredClaimsAsync(CancellationToken.None);
        }

        int secondSweep;
        await using (var db = CreateDbContext())
        {
            secondSweep = await CreateLeaseRecoveryService(db)
                .RecoverExpiredClaimsAsync(CancellationToken.None);
        }

        Assert.Equal(1, firstSweep);
        Assert.Equal(0, secondSweep);

        await using var verify = CreateDbContext();

        var job = await verify.Jobs.SingleAsync(x => x.JobId == jobId);
        Assert.Equal(JobStatus.Queued, job.Status);

        Assert.Equal(
            1,
            await verify.JobAttempts.CountAsync(x => x.JobId == jobId));

        Assert.Equal(
            1,
            await verify.OutboxMessages.CountAsync(x => x.JobId == jobId));
    }


    [Fact]
    public async Task RecoverExpiredClaimsAsync_RecoveredJobCanBeClaimedAgain()
    {
        await ResetDatabaseAsync();

        var (jobId, oldClaim) =
            await CreateClaimedJobAsync("worker-before-recovery");
        var oldAttemptId = oldClaim.AttemptId!.Value;

        await using (var db = CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE job_attempts
                SET claimed_at = clock_timestamp() - INTERVAL '5 minutes',
                    lease_expires_at = clock_timestamp() - INTERVAL '1 second'
                WHERE attempt_id = {oldAttemptId}
                """);
        }

        await using (var db = CreateDbContext())
        {
            var count = await CreateLeaseRecoveryService(db)
                .RecoverExpiredClaimsAsync(CancellationToken.None);
            Assert.Equal(1, count);
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE jobs
                SET next_eligible_at = clock_timestamp() - INTERVAL '1 second'
                WHERE job_id = {jobId}
                """);

            var request = new ClaimJobRequest
            {
                JobId = jobId,
                ClaimRequestId = Guid.NewGuid(),
                WorkerInstanceId = "worker-after-recovery"
            };

            var result = await CreateClaimJobService(db).ClaimAsync(
                request, CancellationToken.None);

            Assert.Equal(200, result.StatusCode);

            var newClaim = DeserializeClaimResponse(result);
            Assert.Equal("Granted", newClaim.Outcome);
            Assert.NotEqual(oldAttemptId, newClaim.AttemptId);
            Assert.True(
                newClaim.FencingToken!.Value > oldClaim.FencingToken!.Value);
        }

        await using var verify = CreateDbContext();
        Assert.Equal(2, await verify.JobAttempts.CountAsync(
            x => x.JobId == jobId));
        Assert.Equal(1, await verify.JobAttempts.CountAsync(
            x => x.JobId == jobId &&
                 x.Status == JobAttemptStatus.LeaseExpired));
        Assert.Equal(1, await verify.JobAttempts.CountAsync(
            x => x.JobId == jobId &&
                 x.Status == JobAttemptStatus.Running));
    }


    [Fact]
    public async Task RecoverExpiredClaimsAsync_AfterSuccessfulRenewal_LeavesClaimActive()
    {
        await ResetDatabaseAsync();

        const string workerId = "worker-renew-before-recovery";
        var (jobId, claim) = await CreateClaimedJobAsync(workerId);
        var request = CreateRenewalRequest(jobId, claim, workerId);

        await using (var db = CreateDbContext())
        {
            var renewal = await CreateLeaseRenewalService(db).RenewAsync(
                request, CancellationToken.None);

            Assert.Equal(200, renewal.StatusCode);
        }

        int recovered;
        await using (var db = CreateDbContext())
        {
            recovered = await CreateLeaseRecoveryService(db)
                .RecoverExpiredClaimsAsync(CancellationToken.None);
        }

        Assert.Equal(0, recovered);

        await using var verify = CreateDbContext();
        var job = await verify.Jobs.SingleAsync(x => x.JobId == jobId);
        var attempt = await verify.JobAttempts.SingleAsync(
            x => x.AttemptId == claim.AttemptId);

        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(JobAttemptStatus.Running, attempt.Status);
        Assert.True(attempt.LeaseExpiresAt > claim.LeaseExpiresAt);
        Assert.Empty(await verify.OutboxMessages
            .Where(x => x.JobId == jobId)
            .ToListAsync());
    }

    private static LeaseRecoveryService CreateLeaseRecoveryService(
        ForgeDbContext db,
        int maxAttempts = 3)
    {
        return new LeaseRecoveryService(
            db,
            Options.Create(new LeaseRecoveryOptions
            {
                BatchSize = 50,
                MaxAttempts = maxAttempts,
                InitialRetryDelaySeconds = 5,
                MaxRetryDelaySeconds = 300
            }));
    }

    private static LeaseRenewalService CreateLeaseRenewalService(
        ForgeDbContext db)
    {
        return new LeaseRenewalService(
            db,
            Options.Create(new JobClaimOptions
            {
                LeaseDurationSeconds = 60
            }));
    }

    private static RenewLeaseResponse DeserializeRenewResponse(
        RenewLeaseResult result)
    {
        return JsonSerializer.Deserialize<RenewLeaseResponse>(
            result.ResponseBodyJson,
            JsonOptions)!;
    }

    private static RenewLeaseRequest CreateRenewalRequest(
        Guid jobId,
        ClaimJobResponse claim,
        string workerId,
        Guid? requestId = null,
        long? fencingToken = null)
    {
        return new RenewLeaseRequest
        {
            RenewRequestId = requestId ?? Guid.NewGuid(),
            JobId = jobId,
            AttemptId = claim.AttemptId!.Value,
            WorkerInstanceId = workerId,
            FencingToken = fencingToken ?? claim.FencingToken!.Value
        };
    }

    private static async Task<(Guid JobId, ClaimJobResponse Claim)>
        CreateClaimedJobAsync(string workerId)
    {
        var jobId = await CreateQueuedJobAsync();

        var request = new ClaimJobRequest
        {
            JobId = jobId,
            ClaimRequestId = Guid.NewGuid(),
            WorkerInstanceId = workerId
        };

        await using var db = CreateDbContext();
        var result = await CreateClaimJobService(db).ClaimAsync(
            request, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        return (jobId, DeserializeClaimResponse(result));
    }

    private static SubmitJobRequest CreateRequest(
        Guid? requestId = null,
        string jobType = "log-analysis",
        string text = "database connection failed")
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new { text }));

        return new SubmitJobRequest
        {
            SubmissionRequestId = requestId ?? Guid.NewGuid(),
            JobType = jobType,
            InputPayload = document.RootElement.Clone()
        };
    }

    private static ForgeDbContext CreateDbContext()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                "FORGE_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Set FORGE_TEST_CONNECTION_STRING for integration tests.");

        var builder =
            new NpgsqlConnectionStringBuilder(connectionString);

        if (!string.Equals(
                builder.Database,
                "forge_test",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Integration tests may only use the forge_test database.");
        }

        var options = new DbContextOptionsBuilder<ForgeDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;

        return new ForgeDbContext(options);
    }

    private static async Task ResetDatabaseAsync()
    {
        await using var db = CreateDbContext();

        await db.Database.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                lease_renewal_requests,
                claim_requests,
                job_attempts,
                outbox_messages,
                submission_requests,
                jobs
            RESTART IDENTITY CASCADE
            """);
    }
}
