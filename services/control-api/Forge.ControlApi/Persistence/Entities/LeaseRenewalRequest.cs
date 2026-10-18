namespace Forge.ControlApi.Persistence.Entities;

public sealed class LeaseRenewalRequest
{
    public Guid RenewRequestId { get; set; }

    // These request identifiers are intentionally not foreign keys.
    // Even a NotFound outcome must be retained for idempotent retries.
    public Guid JobId { get; set; }

    public Guid AttemptId { get; set; }

    public string WorkerInstanceId { get; set; } = string.Empty;

    public long FencingToken { get; set; }

    public string RequestFingerprint { get; set; } = string.Empty;

    public LeaseRenewalOutcome Outcome { get; set; }

    // The expiry recorded for the original renewal operation.
    // Null when the renewal was rejected.
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public DateTimeOffset DecidedAt { get; set; }
}
