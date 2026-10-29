using Shop.Modules.Orders.Domain;

namespace Shop.Modules.Orders.Reading;

// One row of GET /orders; GET /orders/{id} returns the lines and history
internal sealed record OrderSummaryResponse(
    Guid Id, Guid CustomerId, OrderStatus Status, decimal Total, int ItemCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
