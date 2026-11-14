namespace Shop.Messaging.Publishing;

/// <summary>
/// Publishes events to <see cref="ShopTopology.EventsExchange"/> and reports which ones RabbitMQ confirmed.
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes the messages as persistent, in list order, and waits for RabbitMQ to confirm each one. Returns one
    /// result per message, in the same order. RabbitMQ being down, refusing a message, or routing it to no queue
    /// fails that message's result rather than throwing; only cancelling <paramref name="cancellationToken"/> throws.
    /// </summary>
    /// <remarks>
    /// A message whose confirm didn't arrive in time may still have been stored, so retrying it can deliver it
    /// twice: delivery is at least once, and consumers drop repeats by <see cref="OutgoingMessage.MessageId"/>.
    /// </remarks>
    Task<IReadOnlyList<PublishResult>> PublishAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken);
}
