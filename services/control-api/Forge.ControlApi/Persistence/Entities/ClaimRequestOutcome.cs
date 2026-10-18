namespace Forge.ControlApi.Persistence.Entities;

public enum ClaimRequestOutcome
{
    Granted,
    AlreadyOwnedBySameRequest,
    AlreadyClaimed,
    NotEligible,
    NotFound,
    ExpiredOrLost
}
