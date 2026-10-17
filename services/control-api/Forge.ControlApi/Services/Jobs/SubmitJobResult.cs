namespace Forge.ControlApi.Services.Jobs;

public sealed record SubmitJobResult(
    int StatusCode,
    string ResponseBodyJson);
