namespace Forge.ControlApi.Services.Jobs;

public sealed class LeaseRecoveryOptions
{
    public const string SectionName = "LeaseRecovery";

    public int PollIntervalMilliseconds { get; set; } = 1000;

    public int BatchSize { get; set; } = 50;

    public int MaxAttempts { get; set; } = 3;

    public int InitialRetryDelaySeconds { get; set; } = 5;

    public int MaxRetryDelaySeconds { get; set; } = 300;
}
