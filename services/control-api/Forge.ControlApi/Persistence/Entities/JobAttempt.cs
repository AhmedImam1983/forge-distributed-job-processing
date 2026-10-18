namespace Forge.ControlApi.Persistence.Entities;

public sealed class JobAttempt
{
    public Guid AttemptId { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }

    public int AttemptNumber { get; set; }

    public string WorkerInstanceId { get; set; } = string.Empty;

    public long FencingToken { get; set; }

    public JobAttemptStatus Status { get; set; } = JobAttemptStatus.Running;

    public DateTimeOffset ClaimedAt { get; set; }

    public DateTimeOffset LeaseExpiresAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureMessage { get; set; }
}
