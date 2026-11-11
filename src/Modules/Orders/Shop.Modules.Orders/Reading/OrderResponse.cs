using System.Linq.Expressions;
using Shop.Modules.Orders.Domain;

namespace Shop.Modules.Orders.Reading;

internal sealed record OrderResponse(
    Guid Id, Guid CustomerId, OrderStatus Status, decimal Total, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<OrderLineResponse> Lines, IReadOnlyList<OrderStatusChangeResponse> History)
{
    /// <summary>
    /// The one mapping from an order to its response. EF translates it into the query that reads an order, so only
    /// these columns are selected and nothing is tracked; <see cref="From"/> runs the compiled form on an order already
    /// in memory, just placed or changed.
    /// </summary>
    public static readonly Expression<Func<Order, OrderResponse>> Projection = order => new OrderResponse(
        order.Id, order.CustomerId, order.Status, order.Total, order.CreatedAt, order.UpdatedAt,
        order.Lines.OrderBy(line => line.Id)
            .Select(line => new OrderLineResponse(line.ProductId, line.ProductName, line.UnitPrice, line.Quantity, line.LineTotal))
            .ToList(),
        order.History.OrderBy(change => change.ChangedAt).ThenBy(change => change.Id)
            .Select(change => new OrderStatusChangeResponse(change.FromStatus, change.ToStatus, change.ChangedBy, change.ChangedAt))
            .ToList());

    private static readonly Func<Order, OrderResponse> FromOrder = Projection.Compile();

    public static OrderResponse From(Order order) => FromOrder(order);
}

internal sealed record OrderLineResponse(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity, decimal LineTotal);

internal sealed record OrderStatusChangeResponse(OrderStatus? FromStatus, OrderStatus ToStatus, Guid ChangedBy, DateTimeOffset ChangedAt);
