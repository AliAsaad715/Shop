namespace Shop.Modules.Orders.Domain;

/// <summary>
/// Pending → Confirmed → Shipped → Delivered, and Pending or Confirmed → Cancelled.
/// Delivered and Cancelled are final; staying in the same status is not a transition.
/// </summary>
internal static class OrderStatusTransitions
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> _allowed = new()
    {
        [OrderStatus.Pending] = [OrderStatus.Confirmed, OrderStatus.Cancelled],
        [OrderStatus.Confirmed] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered],
        [OrderStatus.Delivered] = [],
        [OrderStatus.Cancelled] = [],
    };

    public static bool IsAllowed(OrderStatus from, OrderStatus to) =>
        _allowed.TryGetValue(from, out var next) && next.Contains(to);
}
