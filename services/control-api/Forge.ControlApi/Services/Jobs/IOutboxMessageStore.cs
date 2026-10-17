namespace Forge.ControlApi.Services.Jobs;

public interface IOutboxMessageStore
{
    Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimBatchAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> MarkPublishedAsync(
        Guid outboxMessageId,
        string leaseOwner,
        CancellationToken cancellationToken);

    Task<bool> MarkFailedAsync(
        Guid outboxMessageId,
        string leaseOwner,
        string error,
        TimeSpan retryDelay,
        CancellationToken cancellationToken);
}

public sealed record ClaimedOutboxMessage(
    Guid OutboxMessageId,
    Guid JobId,
    string MessageType,
    string PayloadJson,
    string LeaseOwner,
    int AttemptCount);
