namespace Forge.ControlApi.Contracts.Jobs;

public sealed record JobSubmissionRejection(
    Guid SubmissionRequestId,
    string Outcome,
    string ErrorCode,
    string Message);
