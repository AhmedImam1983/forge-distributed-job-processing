using Forge.ControlApi.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Forge.ControlApi.Persistence;

public sealed class PostgresOutboxMessageStore : IOutboxMessageStore
{
    private readonly ForgeDbContext _dbContext;

    public PostgresOutboxMessageStore(ForgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimBatchAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                "Batch size must be between 1 and 100.");
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration));
        }

        // A fresh token per claim batch prevents a stale claimant
        // from updating a message after another claimant takes over.
        var leaseOwner =
            $"forge-publisher:{Guid.NewGuid():N}";

        const string sql = """
            WITH candidates AS
            (
                SELECT outbox_message_id
                FROM outbox_messages
                WHERE published_at IS NULL
                  AND next_attempt_at <= CURRENT_TIMESTAMP
                  AND (
                      lease_expires_at IS NULL
                      OR lease_expires_at <= CURRENT_TIMESTAMP
                  )
                ORDER BY next_attempt_at, created_at, outbox_message_id
                FOR UPDATE SKIP LOCKED
                LIMIT @batch_size
            )
            UPDATE outbox_messages AS message
            SET lease_owner = @lease_owner,
                lease_expires_at =
                    CURRENT_TIMESTAMP + (@lease_seconds * INTERVAL '1 second'),
                attempt_count = message.attempt_count + 1
            FROM candidates
            WHERE message.outbox_message_id =
                  candidates.outbox_message_id
            RETURNING
                message.outbox_message_id,
                message.job_id,
                message.message_type,
                message.payload_json::text,
                message.lease_owner,
                message.attempt_count;
            """;

        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(
                cancellationToken);

        var connection = (NpgsqlConnection)
            _dbContext.Database.GetDbConnection();

        var dbTransaction = (NpgsqlTransaction)
            transaction.GetDbTransaction();

        var messages = new List<ClaimedOutboxMessage>();

        await using (var command = new NpgsqlCommand(
            sql,
            connection,
            dbTransaction))
        {
            command.Parameters.AddWithValue(
                "batch_size",
                batchSize);

            command.Parameters.AddWithValue(
                "lease_owner",
                leaseOwner);

            command.Parameters.AddWithValue(
                "lease_seconds",
                leaseDuration.TotalSeconds);

            await using var reader =
                await command.ExecuteReaderAsync(
                    cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add(new ClaimedOutboxMessage(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5)));
            }
        }

        // Commit the claim before publishing anything to RabbitMQ.
        await transaction.CommitAsync(cancellationToken);

        return messages;
    }

    public async Task<bool> MarkPublishedAsync(
        Guid outboxMessageId,
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        var affected = await _dbContext.Database
            .ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE outbox_messages
                SET published_at = CURRENT_TIMESTAMP,
                    lease_owner = NULL,
                    lease_expires_at = NULL,
                    last_error = NULL
                WHERE outbox_message_id = {outboxMessageId}
                  AND published_at IS NULL
                  AND lease_owner = {leaseOwner}
                """,
                cancellationToken);

        return affected == 1;
    }

    public async Task<bool> MarkFailedAsync(
        Guid outboxMessageId,
        string leaseOwner,
        string error,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (retryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryDelay));
        }

        var lastError = error.Length <= 2000
            ? error
            : error[..2000];

        var affected = await _dbContext.Database
            .ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE outbox_messages
                SET lease_owner = NULL,
                    lease_expires_at = NULL,
                    next_attempt_at =
                        CURRENT_TIMESTAMP
                        + ({retryDelay.TotalSeconds} * INTERVAL '1 second'),
                    last_error = {lastError}
                WHERE outbox_message_id = {outboxMessageId}
                  AND published_at IS NULL
                  AND lease_owner = {leaseOwner}
                """,
                cancellationToken);

        return affected == 1;
    }
}
