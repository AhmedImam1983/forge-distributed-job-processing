namespace Forge.ControlApi.Persistence.Entities;

public enum CompletionOutcome
{
    AcceptedSucceeded,
    AcceptedRetryScheduled,
    AcceptedFailed,
    AcceptedCancelled,
    LeaseExpiredOrLost,
    NotOwnerOrSuperseded,
    CancellationPending,
    AttemptAlreadyFinalized,
    NotFound,
    IdempotencyKeyConflict,
    InvalidOutcomeForState
}
