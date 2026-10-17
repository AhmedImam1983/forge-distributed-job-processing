using System.Text.Json;
using Forge.ControlApi.Contracts.Jobs;
using Forge.ControlApi.Persistence;
using Forge.ControlApi.Persistence.Entities;
using Forge.ControlApi.Services.Jobs;
using Microsoft.EntityFrameworkCore;
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
                outbox_messages,
                submission_requests,
                jobs
            RESTART IDENTITY CASCADE
            """);
    }
}
