namespace Shop.Messaging.Publishing;

/// <summary>
/// One event to publish to <see cref="ShopTopology.EventsExchange"/>.
/// </summary>
/// <param name="MessageId">
/// Stable across retries (the outbox row's ID in R4-02), so a consumer that receives the message twice can tell.
/// </param>
/// <param name="RoutingKey">The event's type, such as <see cref="ShopTopology.RoutingKeys.OrderPlaced"/>.</param>
/// <param name="Body">The event as UTF-8 JSON.</param>
/// <param name="OccurredAt">When the event happened, not when it was published.</param>
public sealed record OutgoingMessage(Guid MessageId, string RoutingKey, ReadOnlyMemory<byte> Body, DateTimeOffset OccurredAt);
