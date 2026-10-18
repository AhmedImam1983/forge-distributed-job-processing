namespace Forge.ControlApi.Services.Jobs;

public sealed record CompleteJobResult(
    int StatusCode,
    string ResponseBodyJson);
