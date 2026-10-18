namespace Forge.ControlApi.Persistence.Entities;

public sealed class ClaimRequest
{
    public Guid ClaimRequestId { get; set; }

    // Intentionally has no Job foreign key: NotFound outcomes must be durable.
    public Guid JobId { get; set; }

    public string WorkerInstanceId { get; set; } = string.Empty;

    public string RequestFingerprint { get; set; } = string.Empty;

    public ClaimRequestOutcome Outcome { get; set; }

    public Guid? AttemptId { get; set; }

    public long? FencingToken { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public DateTimeOffset DecidedAt { get; set; }
}
