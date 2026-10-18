namespace Forge.ControlApi.Persistence.Entities;

public enum LeaseRenewalOutcome
{
    Renewed,
    LeaseExpiredOrLost,
    CancellationPending,
    NotOwnerOrSuperseded,
    NotFound
}
