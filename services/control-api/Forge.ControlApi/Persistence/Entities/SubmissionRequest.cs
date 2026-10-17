namespace Forge.ControlApi.Persistence.Entities;

public class SubmissionRequest
{
    public string ClientScopeId { get; set; } = string.Empty;

    public Guid SubmissionRequestId { get; set; }

    public string RequestFingerprint { get; set; } = string.Empty;

    public SubmissionOutcome Outcome { get; set; }

    public int ResponseStatusCode { get; set; }

    public string ResponseBodyJson { get; set; } = "{}";

    public Guid? JobId { get; set; }

    public DateTimeOffset DecidedAt { get; set; }
}