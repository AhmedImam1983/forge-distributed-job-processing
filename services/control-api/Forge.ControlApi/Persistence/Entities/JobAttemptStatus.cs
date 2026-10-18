namespace Forge.ControlApi.Persistence.Entities;

public enum JobAttemptStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    LeaseExpired
}
