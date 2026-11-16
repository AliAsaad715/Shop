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
    public static IServiceCollection AddQueueConsumer<THandler>(this IServiceCollection services, string queue, ushort prefetch = 5)
        where THandler : class, IMessageHandler
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentOutOfRangeException.ThrowIfZero(prefetch);

        services.AddScoped<THandler>();
        services.AddHostedService(provider => new QueueConsumer<THandler>(
            queue,
            prefetch,
            provider.GetRequiredService<RabbitMqConnection>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<QueueConsumer<THandler>>>()));
        return services;
    }
}
