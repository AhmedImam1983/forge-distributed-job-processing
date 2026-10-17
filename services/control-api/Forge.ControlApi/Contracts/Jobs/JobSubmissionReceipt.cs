namespace Forge.ControlApi.Contracts.Jobs;

public sealed record JobSubmissionReceipt(
    Guid SubmissionRequestId,
    Guid JobId,
    string Outcome,
    DateTimeOffset AcceptedAt);
