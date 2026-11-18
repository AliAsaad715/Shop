namespace Shop.Messaging.Publishing;

/// <summary>
/// What happened to one logical event. <see cref="Confirmed"/> requires every physical publish to succeed, including
/// the mandatory email copy when required. Anything else must be published again later, with the same message ID.
/// </summary>
/// <param name="Failure">Why it wasn't confirmed, for logs; null when confirmed.</param>
public sealed record PublishResult(Guid MessageId, bool Confirmed, string? Failure)
{
    public static PublishResult Success(Guid messageId) => new(messageId, true, null);

    public static PublishResult Failed(Guid messageId, string failure) => new(messageId, false, failure);
}
