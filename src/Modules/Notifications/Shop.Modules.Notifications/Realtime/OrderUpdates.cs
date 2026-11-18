namespace Shop.Modules.Notifications.Realtime;

/// <summary>
/// The client methods the hubs call, which the live pages listen for (task R4-08); R4-07 sends them. Each carries the
/// event from <c>Shop.Modules.Orders.Contracts</c> as SignalR's camelCase JSON, the same shape as the outbox message.
/// </summary>
internal static class OrderUpdates
{
    /// <summary>A new order, with an <c>OrderPlacedEvent</c>: to the admins group (<see cref="AdminHub"/>).</summary>
    public const string OrderPlaced = "orderPlaced";

    /// <summary>
    /// A status change, with an <c>OrderStatusChangedEvent</c>: to the admins group, and to the order's customer through
    /// <c>Clients.User</c> on <see cref="OrdersHub"/>. A page ignores one whose version isn't newer than what it shows.
    /// </summary>
    public const string OrderStatusChanged = "orderStatusChanged";
}
