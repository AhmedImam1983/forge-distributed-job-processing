namespace Forge.ControlApi.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";

    public int Port { get; set; } = 5673;

    public string VirtualHost { get; set; } = "/";

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string ExchangeName { get; set; } = "forge.jobs";

    public string QueueName { get; set; } =
        "forge.job-available.v1";

    public string RoutingKey { get; set; } =
        "job.available.v1";
}
