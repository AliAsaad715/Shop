namespace Shop.Messaging.Consuming;

/// <summary>
/// Handles messages from one queue (<see cref="ConsumerRegistration.AddQueueConsumer{THandler}"/>). Each message gets
/// its own DI scope, and the handler's answer decides what RabbitMQ does with it. A handler that throws counts as
/// <see cref="MessageOutcome.Retry"/>.
/// </summary>
public interface IMessageHandler
{
    /// <param name="cancellationToken">Cancelled only when shutdown runs out of time; until then the current message
    /// is allowed to finish.</param>
    Task<MessageOutcome> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken);
}

/// <summary>What RabbitMQ should do with a message once its handler is done.</summary>
public enum MessageOutcome
{
    /// <summary>Handled, or a repeat of one already handled: acknowledge it, and RabbitMQ deletes it.</summary>
    Done,

    /// <summary>
    /// Failed in a way a later try may fix: <c>basic.reject</c> with requeue. RabbitMQ counts it as a failed delivery
    /// and, past the queue's delivery limit, moves it to the dead-letter queue.
    /// </summary>
    Retry,

    /// <summary>Can never succeed, such as an unreadable message: <c>basic.reject</c> without requeue, so it goes
    /// straight to the dead-letter queue.</summary>
    Reject,
}

/// <param name="MessageId">The publisher's message ID (the outbox row ID); null when missing or not a GUID.</param>
/// <param name="Type">The routing key, such as <c>order.placed</c>.</param>
/// <param name="Body">A copy of the body, so the handler may keep it.</param>
/// <param name="FailedDeliveries">How many earlier deliveries of this message failed (<c>x-delivery-count</c>).</param>
public sealed record ReceivedMessage(Guid? MessageId, string Type, ReadOnlyMemory<byte> Body, int FailedDeliveries);
