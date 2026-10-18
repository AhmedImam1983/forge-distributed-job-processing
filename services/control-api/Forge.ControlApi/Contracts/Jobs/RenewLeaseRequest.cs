namespace Forge.ControlApi.Contracts.Jobs;

public sealed class RenewLeaseRequest
{
    public Guid RenewRequestId { get; set; }

    public Guid JobId { get; set; }

    public Guid AttemptId { get; set; }

    public string WorkerInstanceId { get; set; } = string.Empty;

    public long FencingToken { get; set; }
}
