using Microsoft.Extensions.Options;

namespace Forge.ControlApi.Services.Jobs;

public sealed class LeaseRecoveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LeaseRecoveryWorker> _logger;
    private readonly LeaseRecoveryOptions _options;

    public LeaseRecoveryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<LeaseRecoveryWorker> logger,
        IOptions<LeaseRecoveryOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation("Lease recovery worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();

                var recovery = scope.ServiceProvider
                    .GetRequiredService<LeaseRecoveryService>();

                var count = await recovery.RecoverExpiredClaimsAsync(
                    stoppingToken);

                if (count > 0)
                {
                    _logger.LogInformation(
                        "Lease recovery processed {Count} expired claims.",
                        count);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Lease recovery iteration failed.");
            }

            try
            {
                await Task.Delay(
                    _options.PollIntervalMilliseconds,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Lease recovery worker stopped.");
    }
}
