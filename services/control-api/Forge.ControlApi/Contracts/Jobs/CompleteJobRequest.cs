using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Forge.ControlApi.Contracts.Jobs;

public sealed class CompleteJobRequest
{
    public Guid CompletionRequestId { get; set; }

    public Guid JobId { get; set; }

    public Guid AttemptId { get; set; }

    [Required]
    [MaxLength(200)]
    public string WorkerInstanceId { get; set; } = string.Empty;

    public long FencingToken { get; set; }

    [Required]
    [MaxLength(40)]
    public string Outcome { get; set; } = string.Empty;

    public JsonElement? ResultDescriptor { get; set; }

    [MaxLength(100)]
    public string? FailureCode { get; set; }

    [MaxLength(2000)]
    public string? FailureMessage { get; set; }

    [MaxLength(2000)]
    public string? CancellationDetails { get; set; }
}
