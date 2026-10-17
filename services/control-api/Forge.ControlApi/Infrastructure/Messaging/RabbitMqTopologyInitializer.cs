using Microsoft.Extensions.Options;

namespace Forge.ControlApi.Infrastructure.Messaging;

public sealed class RabbitMqTopologyInitializer : BackgroundService
{
    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly ILogger<RabbitMqTopologyInitializer> _logger;

    public RabbitMqTopologyInitializer(
        RabbitMqConnectionProvider connectionProvider,
        ILogger<RabbitMqTopologyInitializer> logger)
    {
        _connectionProvider = connectionProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _connectionProvider.EnsureTopologyAsync(
                    stoppingToken);

                _logger.LogInformation(
                    "RabbitMQ topology is ready.");

                return;
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "RabbitMQ initialization failed. " +
                    "The outbox remains in PostgreSQL. " +
                    "Retrying in 5 seconds.");

                await Task.Delay(
                    TimeSpan.FromSeconds(5),
                    stoppingToken);
            }
        }
    }
}
