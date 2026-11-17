namespace Shop.Messaging.Publishing;

/// <summary>
/// Publishes live events and any required durable email copies, and reports which logical events RabbitMQ confirmed.
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes the messages as persistent, in list order, and waits for RabbitMQ to confirm each one. Returns one
    /// result per logical event, in the same order. RabbitMQ being down, refusing a message, or failing to route a
    /// required email copy (<see cref="ShopTopology.RequiresEmail"/>) to its exact queue fails the result rather than
    /// throwing; only cancelling <paramref name="cancellationToken"/> throws.
    /// </summary>
    /// <remarks>
    /// A message whose confirm didn't arrive in time may still have been stored, so retrying it can deliver it
    /// twice. A partial success across destinations can also duplicate a copy on retry: delivery is at least once,
    /// and consumers drop repeats by <see cref="OutgoingMessage.MessageId"/>.
    /// </remarks>
    Task<IReadOnlyList<PublishResult>> PublishAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken);
}
