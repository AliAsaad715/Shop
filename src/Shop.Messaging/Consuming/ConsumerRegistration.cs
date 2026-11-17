using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shop.Messaging.Consuming;

public static class ConsumerRegistration
{
    /// <summary>
    /// Consumes <paramref name="queue"/> in the background with manual acknowledgements, handing each message to a
    /// new <typeparamref name="THandler"/> in its own scope. Needs <see cref="MessagingRegistration.AddShopMessaging"/>.
    /// </summary>
    /// <param name="prefetch">How many unacknowledged messages RabbitMQ sends at once. Small, so a crash returns only
    /// a handful to the queue and another worker can take the rest.</param>
    /// <param name="drainTimeout">How long a graceful stop waits for the message in progress; 20 seconds by default,
    /// inside compose's 30-second stop_grace_period.</param>
    public static IServiceCollection AddQueueConsumer<THandler>(
        this IServiceCollection services, string queue, ushort prefetch = 5, TimeSpan? drainTimeout = null)
        where THandler : class, IMessageHandler
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentOutOfRangeException.ThrowIfZero(prefetch);

        services.AddScoped<THandler>();
        services.AddHostedService(provider => new QueueConsumer<THandler>(
            queue,
            prefetch,
            drainTimeout ?? QueueConsumer<THandler>.DefaultDrainTimeout,
            provider.GetRequiredService<RabbitMqConnection>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<QueueConsumer<THandler>>>()));
        return services;
    }
}
