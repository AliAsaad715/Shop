namespace Shop.Modules.Notifications.Processing;

/// <summary>A message the worker has already handled, so a repeat of it is acknowledged without sending again.</summary>
internal sealed class ProcessedMessage
{
    public const int MaxTypeLength = 100;

    /// <summary>The publisher's message ID: the Orders outbox row ID.</summary>
    public Guid MessageId { get; init; }

    /// <summary>The routing key, such as <c>order.placed</c>, for whoever inspects the table.</summary>
    public string Type { get; init; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; init; }
}
