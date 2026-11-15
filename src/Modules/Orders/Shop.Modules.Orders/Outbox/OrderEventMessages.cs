using System.Diagnostics;
using System.Text.Json;
using Shop.Messaging;
using Shop.Modules.Orders.Contracts;
using Shop.Modules.Orders.Domain;

namespace Shop.Modules.Orders.Outbox;

/// <summary>
/// Turns an order's events into outbox rows: the type becomes the routing key, and the payload is the public event
/// from <c>Shop.Modules.Orders.Contracts</c> as JSON, so consumers never see the module's domain types.
/// </summary>
internal static class OrderEventMessages
{
    // camelCase, like the API
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static OutboxMessage ToOutboxMessage(OrderEvent orderEvent) => orderEvent switch
    {
        OrderPlaced placed => new OutboxMessage(
            ShopTopology.RoutingKeys.OrderPlaced,
            JsonSerializer.Serialize(new OrderPlacedEvent(
                placed.OrderId, placed.CustomerId, placed.CustomerEmail, placed.Status.ToString(), placed.Total,
                [.. placed.Lines.Select(line => new OrderPlacedLine(line.ProductId, line.ProductName, line.UnitPrice, line.Quantity, line.LineTotal))],
                placed.Version, placed.OccurredAt), Json),
            placed.OccurredAt),
        OrderStatusChanged changed => new OutboxMessage(
            ShopTopology.RoutingKeys.OrderStatusChanged,
            JsonSerializer.Serialize(new OrderStatusChangedEvent(
                changed.OrderId, changed.CustomerId, changed.FromStatus.ToString(), changed.ToStatus.ToString(),
                changed.Version, changed.ChangedBy, changed.OccurredAt), Json),
            changed.OccurredAt),
        _ => throw new UnreachableException($"No outbox message is defined for {orderEvent.GetType().Name}."),
    };
}
