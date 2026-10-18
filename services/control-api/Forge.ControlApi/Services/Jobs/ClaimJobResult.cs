namespace Forge.ControlApi.Services.Jobs;

public sealed record ClaimJobResult(
    int StatusCode,
    string ResponseBodyJson);
