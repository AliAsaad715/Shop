namespace Shop.Modules.Orders.Outbox;

/// <summary>
/// An event waiting to be published, saved in the same transaction as the order change it describes. The relay
/// publishes it to RabbitMQ and then sets <see cref="PublishedAt"/>.
/// </summary>
internal sealed class OutboxMessage
{
    public const int MaxTypeLength = 100;
    public const int MaxErrorLength = 1000;

    private OutboxMessage() { }

    public OutboxMessage(string type, string payload, DateTimeOffset occurredAt)
    {
        Id = Guid.CreateVersion7();
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    /// <summary>Also the RabbitMQ message ID, so a consumer can spot a message it received twice.</summary>
    public Guid Id { get; private set; }

    /// <summary>The event type, which is also its routing key, such as <c>order.placed</c>.</summary>
    public string Type { get; private set; } = string.Empty;

    /// <summary>
    /// The event as JSON. The column is jsonb, so Postgres keeps its own normalized copy (keys reordered, spaces
    /// added); that copy is the body RabbitMQ delivers.
    /// </summary>
    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>When RabbitMQ confirmed it; null while it waits to be published.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>How many times the relay has tried to publish it, the successful try included.</summary>
    public int Attempts { get; private set; }

    /// <summary>Why the last try failed, for whoever looks at a row that is stuck; null once published.</summary>
    public string? LastError { get; private set; }

    public void MarkPublished(DateTimeOffset now)
    {
        Attempts++;
        PublishedAt = now;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        Attempts++;
        LastError = error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
    }
}
