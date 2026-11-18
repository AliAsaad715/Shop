using Shop.Modules.Orders.Domain;

namespace Shop.Modules.Orders.Reading;

// One row of GET /orders; GET /orders/{id} returns the lines and history. Version goes up with each status change, so a
// live page can ignore an event older than what it shows (task R4-08)
internal sealed record OrderSummaryResponse(
    Guid Id, Guid CustomerId, OrderStatus Status, decimal Total, int ItemCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int Version);
