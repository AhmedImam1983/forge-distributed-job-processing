namespace Forge.ControlApi.Persistence.Entities;

public sealed class CompletionRequest
{
    public Guid CompletionRequestId { get; set; }

    // No foreign keys: rejected NotFound requests must be durable.
    public Guid JobId { get; set; }

    public Guid AttemptId { get; set; }

    public string WorkerInstanceId { get; set; } = string.Empty;

    public long FencingToken { get; set; }

    public string RequestFingerprint { get; set; } = string.Empty;

    // Stores the original request parameters for durable idempotency.
    public string RequestPayloadJson { get; set; } = "{}";

    public CompletionOutcome Outcome { get; set; }

    public DateTimeOffset DecidedAt { get; set; }
}
