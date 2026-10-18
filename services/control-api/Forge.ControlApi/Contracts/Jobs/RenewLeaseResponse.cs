namespace Forge.ControlApi.Contracts.Jobs;

public sealed record RenewLeaseResponse(
    Guid RenewRequestId,
    Guid JobId,
    Guid AttemptId,
    string Outcome,
    string? OriginalOutcome,
    string CurrentClaimStatus,
    DateTimeOffset? LeaseExpiresAt,
    bool IsReplay,
    string? Message = null);
