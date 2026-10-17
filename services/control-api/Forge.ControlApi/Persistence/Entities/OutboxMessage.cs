namespace Forge.ControlApi.Persistence.Entities;

public static class OutboxMessageTypes
{
    public const string JobAvailableV1 = "job.available.v1";
}

public class OutboxMessage
{
    public Guid OutboxMessageId { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }

    public string MessageType { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public string? LastError { get; set; }
}
