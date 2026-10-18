namespace Forge.ControlApi.Persistence.Entities;

public class Job
{
    public Guid JobId { get; set; } = Guid.NewGuid();

    public string JobType { get; set; } = string.Empty;

    public JobStatus Status { get; set; } = JobStatus.Queued;

    public string? InputPayloadJson { get; set; }

    public string? InputReference { get; set; }

    public string? InputRevision { get; set; }

    public string? ResultDescriptorJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset NextEligibleAt { get; set; }

    // Monotonically increases whenever a new execution claim is granted.
    public long LastFencingToken { get; set; }
}