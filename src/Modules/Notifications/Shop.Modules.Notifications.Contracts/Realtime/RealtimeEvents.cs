namespace Shop.Modules.Notifications.Contracts.Realtime;

/// <summary>Server-to-client method names on the notifications hubs.</summary>
public static class RealtimeEvents
{
    public const string OrderPlaced = "OrderPlaced";
    public const string OrderStatusChanged = "OrderStatusChanged";

    /// <summary>No arguments. Reload current state after a live subscription starts or recovers.</summary>
    public const string ResyncRequired = "ResyncRequired";
}

/// <summary>An admin dashboard row, without the email or line details carried by the broker event.</summary>
public sealed record OrderPlacedNotification(
    Guid MessageId, Guid OrderId, int Version, Guid CustomerId, string Status,
    decimal Total, int ItemCount, DateTimeOffset PlacedAt);

/// <summary>A customer's status update. Ignore versions older than or equal to the state already displayed.</summary>
public sealed record OrderStatusChangedNotification(
    Guid MessageId, Guid OrderId, int Version, string FromStatus, string Status, DateTimeOffset ChangedAt);
