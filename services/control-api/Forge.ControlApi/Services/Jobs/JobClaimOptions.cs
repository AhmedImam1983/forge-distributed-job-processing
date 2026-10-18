namespace Forge.ControlApi.Services.Jobs;

public sealed class JobClaimOptions
{
    public const string SectionName = "JobClaims";

    public int LeaseDurationSeconds { get; set; } = 60;
}
