namespace Forge.ControlApi.Contracts.Jobs;

public sealed class ClaimJobRequest
{
    public Guid JobId { get; set; }

    public Guid ClaimRequestId { get; set; }

    public string WorkerInstanceId { get; set; } = string.Empty;
}
