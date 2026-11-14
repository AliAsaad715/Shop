namespace Shop.Messaging.Publishing;

/// <summary>
/// What happened to one message. Only <see cref="Confirmed"/> means RabbitMQ stored it in every queue it routes to;
/// anything else must be published again later.
/// </summary>
/// <param name="Failure">Why it wasn't confirmed, for logs; null when confirmed.</param>
public sealed record PublishResult(Guid MessageId, bool Confirmed, string? Failure)
{
    public static PublishResult Success(Guid messageId) => new(messageId, true, null);

    public static PublishResult Failed(Guid messageId, string failure) => new(messageId, false, failure);
}
