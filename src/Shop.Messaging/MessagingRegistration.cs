using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Messaging.Publishing;

namespace Shop.Messaging;

public static class MessagingRegistration
{
    /// <summary>
    /// Registers the RabbitMQ connection, the topology declared on connect, <see cref="IEventPublisher"/> and a
    /// <c>rabbitmq</c> health check. A missing or invalid <c>RabbitMq</c> section stops the host at startup; RabbitMQ
    /// itself being down doesn't.
    /// </summary>
    /// <param name="clientName">The connection's name in the management UI, such as <c>shop-api</c>.</param>
    /// <param name="failureStatus">
    /// The health status while disconnected: Degraded for the API, which keeps working without RabbitMQ.
    /// </param>
    public static IServiceCollection AddShopMessaging(
        this IServiceCollection services, string clientName, HealthStatus failureStatus = HealthStatus.Degraded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        services.AddOptions<RabbitMqOptions>()
            .BindConfiguration(RabbitMqOptions.Section)
            .Validate(RabbitMqOptions.HasServer,
                "Set RabbitMq__Host (such as rabbitmq), and keep RabbitMq__Port between 1 and 65535 and RabbitMq__VirtualHost non-empty.")
            .Validate(RabbitMqOptions.HasCredentials,
                "Set RabbitMq__UserName and RabbitMq__Password; RabbitMQ's guest user only works from the broker's own machine.")
            .Validate(RabbitMqOptions.HasValidTimeouts,
                "RabbitMq:ConnectTimeout and ConfirmTimeout must each be more than 0 and at most 60 seconds.")
            .ValidateOnStart();

        services.AddSingleton(provider => new RabbitMqConnection(
            provider.GetRequiredService<IOptions<RabbitMqOptions>>().Value,
            clientName,
            provider.GetRequiredService<ILogger<RabbitMqConnection>>()));
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        services.AddHostedService<RabbitMqConnector>();
        services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq", failureStatus);

        return services;
    }
}
