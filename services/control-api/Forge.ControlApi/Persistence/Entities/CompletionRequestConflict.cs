namespace Forge.ControlApi.Persistence.Entities;

public sealed class CompletionRequestConflict
{
    public Guid CompletionRequestId { get; set; }

    public string RequestFingerprint { get; set; } = string.Empty;

    public string RequestPayloadJson { get; set; } = "{}";

    public CompletionOutcome Outcome { get; set; } =
        CompletionOutcome.IdempotencyKeyConflict;

    public DateTimeOffset DecidedAt { get; set; }
}
