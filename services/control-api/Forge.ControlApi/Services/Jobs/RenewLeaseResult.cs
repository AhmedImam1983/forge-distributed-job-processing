namespace Forge.ControlApi.Services.Jobs;

public sealed record RenewLeaseResult(
    int StatusCode,
    string ResponseBodyJson);
