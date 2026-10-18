using System.Text.Json;

namespace Forge.ControlApi.Contracts.Jobs;

public sealed record ClaimJobResponse(
    Guid ClaimRequestId,
    Guid JobId,
    string Outcome,
    string? OriginalOutcome,
    string? CurrentClaimStatus,
    Guid? AttemptId,
    long? FencingToken,
    DateTimeOffset? LeaseExpiresAt,
    JobExecutionDescriptor? ExecutionDescriptor,
    string? Message = null);

public sealed record JobExecutionDescriptor(
    string JobType,
    JsonElement? InputPayload,
    string? InputReference,
    string? InputRevision);
