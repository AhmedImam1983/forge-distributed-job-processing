namespace Forge.ControlApi.Persistence.Entities;

public enum JobStatus
{
    Queued,
    Running,
    CancelRequested,
    Succeeded,
    Failed,
    Cancelled
}