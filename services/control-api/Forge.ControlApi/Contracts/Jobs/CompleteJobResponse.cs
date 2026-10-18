namespace Forge.ControlApi.Contracts.Jobs;

public sealed record CompleteJobResponse(
    Guid CompletionRequestId,
    Guid JobId,
    Guid AttemptId,
    string Outcome,
    string? OriginalOutcome,
    string? CurrentJobStatus,
    string? CurrentAttemptStatus,
    bool IsReplay,
    string? Message = null);
