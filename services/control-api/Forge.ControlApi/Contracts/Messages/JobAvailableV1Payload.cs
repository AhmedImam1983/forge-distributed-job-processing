namespace Forge.ControlApi.Contracts.Messaging;

public sealed record JobAvailableV1Payload(
    Guid MessageId,
    Guid JobId,
    string JobType);