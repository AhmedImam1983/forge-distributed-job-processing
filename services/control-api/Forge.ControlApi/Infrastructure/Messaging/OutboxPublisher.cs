using System.Text;
using Forge.ControlApi.Services.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Forge.ControlApi.Infrastructure.Messaging;

public sealed class OutboxPublisher : BackgroundService
{
    private const int BatchSize = 10;

    private static readonly TimeSpan LeaseDuration =
        TimeSpan.FromMinutes(2);

    private static readonly TimeSpan IdleDelay =
        TimeSpan.FromSeconds(1);

    private static readonly TimeSpan FailureDelay =
        TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(
        IServiceScopeFactory scopeFactory,
        RabbitMqConnectionProvider connectionProvider,
        IOptions<RabbitMqOptions> options,
        ILogger<OutboxPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionProvider = connectionProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        var topologyReady = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!topologyReady)
                    {
                        await _connectionProvider.EnsureTopologyAsync(
                            stoppingToken);

                        topologyReady = true;

                        _logger.LogInformation(
                            "RabbitMQ topology is ready for the outbox publisher.");
                    }

                    if (channel is null || !channel.IsOpen)
                    {
                        if (channel is not null)
                        {
                            await channel.DisposeAsync();
                            channel = null;
                        }

                        var connection =
                            await _connectionProvider.GetConnectionAsync(
                                stoppingToken);

                        channel = await connection.CreateChannelAsync(
                            new CreateChannelOptions(
                                publisherConfirmationsEnabled: true,
                                publisherConfirmationTrackingEnabled: true),
                            cancellationToken: stoppingToken);

                        _logger.LogInformation(
                            "RabbitMQ publisher channel is ready.");
                    }

                    await using var scope = _scopeFactory.CreateAsyncScope();

                    var store = scope.ServiceProvider
                        .GetRequiredService<IOutboxMessageStore>();

                    var messages = await store.ClaimBatchAsync(
                        BatchSize,
                        LeaseDuration,
                        stoppingToken);

                    if (messages.Count == 0)
                    {
                        await Task.Delay(IdleDelay, stoppingToken);
                        continue;
                    }

                    foreach (var message in messages)
                    {
                        stoppingToken.ThrowIfCancellationRequested();

                        try
                        {
                            if (!channel.IsOpen)
                            {
                                throw new InvalidOperationException(
                                    "RabbitMQ publisher channel is closed.");
                            }

                            var properties = new BasicProperties
                            {
                                MessageId =
                                    message.OutboxMessageId.ToString("D"),

                                Type = message.MessageType,
                                ContentType = "application/json",
                                DeliveryMode = DeliveryModes.Persistent
                            };

                            var body = Encoding.UTF8.GetBytes(
                                message.PayloadJson);

                            // With publisher confirmation tracking enabled,
                            // this await waits for broker confirmation.
                            await channel.BasicPublishAsync(
                                exchange: _options.ExchangeName,
                                routingKey: _options.RoutingKey,
                                mandatory: true,
                                basicProperties: properties,
                                body: body,
                                cancellationToken: stoppingToken);

                            var markedPublished =
                                await store.MarkPublishedAsync(
                                    message.OutboxMessageId,
                                    message.LeaseOwner,
                                    stoppingToken);

                            if (markedPublished)
                            {
                                _logger.LogInformation(
                                    "Published outbox message {MessageId} " +
                                    "for job {JobId}.",
                                    message.OutboxMessageId,
                                    message.JobId);
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Message {MessageId} was acknowledged by " +
                                    "RabbitMQ, but its outbox lease was lost. " +
                                    "A duplicate delivery may occur.",
                                    message.OutboxMessageId);
                            }
                        }
                        catch (OperationCanceledException)
                            when (stoppingToken.IsCancellationRequested)
                        {
                            // The broker might have received the message.
                            // Leave the lease to expire rather than guessing
                            // whether publishing succeeded.
                            return;
                        }
                        catch (Exception exception)
                        {
                            _logger.LogError(
                                exception,
                                "Outbox message {MessageId} could not be " +
                                "published or marked as published.",
                                message.OutboxMessageId);

                            try
                            {
                                var markedFailed =
                                    await store.MarkFailedAsync(
                                        message.OutboxMessageId,
                                        message.LeaseOwner,
                                        $"{exception.GetType().Name}: " +
                                            exception.Message,
                                        GetRetryDelay(message.AttemptCount),
                                        stoppingToken);

                                if (!markedFailed)
                                {
                                    _logger.LogWarning(
                                        "Could not reschedule outbox message " +
                                        "{MessageId}; its lease is no longer owned.",
                                        message.OutboxMessageId);
                                }
                            }
                            catch (OperationCanceledException)
                                when (stoppingToken.IsCancellationRequested)
                            {
                                return;
                            }
                            catch (Exception storeException)
                            {
                                _logger.LogError(
                                    storeException,
                                    "Could not record the failure for outbox " +
                                    "message {MessageId}. The lease will expire.",
                                    message.OutboxMessageId);
                            }

                            if (channel is { IsOpen: false })
                            {
                                topologyReady = false;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    topologyReady = false;

                    _logger.LogError(
                        exception,
                        "Outbox polling or RabbitMQ setup failed. " +
                        "Retrying in {DelaySeconds} seconds.",
                        FailureDelay.TotalSeconds);

                    await Task.Delay(FailureDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync();
            }
        }
    }

    private static TimeSpan GetRetryDelay(int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 6);
        var seconds = Math.Min(5 * Math.Pow(2, exponent), 300);

        return TimeSpan.FromSeconds(seconds);
    }
}
