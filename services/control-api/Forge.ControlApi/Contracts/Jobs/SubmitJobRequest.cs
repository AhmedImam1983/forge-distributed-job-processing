using System.Text.Json;

namespace Forge.ControlApi.Contracts.Jobs;

public sealed record SubmitJobRequest
{
    public Guid SubmissionRequestId { get; init; }

    public string JobType { get; init; } = string.Empty;

    public JsonElement? InputPayload { get; init; }

    public string? InputReference { get; init; }

    public string? InputRevision { get; init; }
}
