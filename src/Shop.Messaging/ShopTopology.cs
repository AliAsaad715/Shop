using RabbitMQ.Client;

namespace Shop.Messaging;

/// <summary>
/// The exchanges, queues and bindings every host relies on. Each host declares them when it connects; declaring
/// them again with the same arguments changes nothing, so start order doesn't matter.
/// </summary>
/// <remarks>
/// A queue's arguments are fixed once it exists. Declaring it again with different ones fails with
/// PRECONDITION_FAILED, so changing an argument here means deleting the queue first (its messages go with it).
/// </remarks>
public static class ShopTopology
{
    /// <summary>The broker's built-in exchange: a mandatory publish with a queue name must reach that exact queue.</summary>
    public const string DefaultExchange = "";

    /// <summary>Every domain event, routed by its type, such as <c>order.placed</c>.</summary>
    public const string EventsExchange = "shop.events";

    /// <summary>Receives messages a queue gave up on, keyed by the name of the queue they came from.</summary>
    public const string DeadLetterExchange = "shop.dlx";

    /// <summary>Order confirmation emails, read by the email worker.</summary>
    public const string EmailQueue = "email.order-confirmation";

    /// <summary>Emails that failed for good, kept until someone inspects them and moves them back.</summary>
    public const string EmailDeadLetterQueue = "email.order-confirmation.dlq";

    /// <summary>
    /// How many failed redeliveries the email queue allows: 4 means the first delivery plus four retries, with the
    /// fifth failure dead-lettered. Set explicitly: since RabbitMQ 4.0, quorum queues default to 20 and then drop the
    /// message when no dead-letter exchange is set.
    /// </summary>
    /// <remarks>
    /// Since RabbitMQ 4.3 only <c>basic.reject</c> with requeue and a consumer's lost connection count as failures.
    /// <c>basic.nack</c> with requeue doesn't count, so a consumer that nacks can retry a message forever: consumers
    /// of this queue must reject.
    /// </remarks>
    public const int EmailDeliveryLimit = 4;

    public static class RoutingKeys
    {
        public const string OrderPlaced = "order.placed";
        public const string OrderStatusChanged = "order.status-changed";

        /// <summary>One word after <c>order.</c>: every order event, for the live-update queues (R4-07).</summary>
        public const string AllOrderEvents = "order.*";
    }

    /// <summary>
    /// Whether an event needs a separately confirmed copy in the durable email queue. A confirmation from a live
    /// queue cannot satisfy this requirement. The copy on <see cref="EventsExchange"/> is optional when no page watches.
    /// </summary>
    internal static bool RequiresEmail(string routingKey) => routingKey == RoutingKeys.OrderPlaced;

    /// <summary>
    /// Creates a temporary live-update queue on its consumer's channel. Call only in an API instance: every
    /// instance needs its own copy of each event. The server-generated name is replaced during automatic recovery.
    /// </summary>
    public static async Task<string> DeclareLiveQueueAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        var queue = await channel.QueueDeclareAsync(
            queue: string.Empty, durable: false, exclusive: true, autoDelete: true,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "classic" },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queue.QueueName, EventsExchange, RoutingKeys.AllOrderEvents, cancellationToken: cancellationToken);
        return queue.QueueName;
    }

    internal static async Task DeclareAsync(IConnection connection, CancellationToken cancellationToken)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(
            DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);

        // The dead-letter queue first, so nothing the email queue dead-letters can arrive before it exists
        await channel.QueueDeclareAsync(
            EmailDeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(EmailDeadLetterQueue, DeadLetterExchange, EmailQueue, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            EmailQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: EmailQueueArguments(),
            cancellationToken: cancellationToken);
        // Upgrade an existing broker without deleting its queue or waiting emails. The publisher now sends the email
        // copy directly; retaining this old binding would enqueue a second copy from the live-event publish.
        await channel.QueueUnbindAsync(EmailQueue, EventsExchange, RoutingKeys.OrderPlaced, cancellationToken: cancellationToken);
    }

    internal static Dictionary<string, object?> EmailQueueArguments() => new()
    {
        // Replicated and durable, and it counts each message's failed deliveries itself
        ["x-queue-type"] = "quorum",
        ["x-delivery-limit"] = EmailDeliveryLimit,
        ["x-dead-letter-exchange"] = DeadLetterExchange,
        ["x-dead-letter-routing-key"] = EmailQueue,
        // The queue keeps a message until the dead-letter queue has confirmed it; the default can lose it on the way.
        // RabbitMQ only honours it together with reject-publish
        ["x-dead-letter-strategy"] = "at-least-once",
        ["x-overflow"] = "reject-publish",
    };
}
