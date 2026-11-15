namespace Shop.Modules.Orders.Domain;

/// <summary>
/// Something that happened to an order. The order records it, and saving the order writes it to the outbox in the
/// same transaction (<see cref="Data.OrdersDbContext"/>). Values are copied when it happens, so a later change to the
/// order can't alter an event that is waiting to be saved.
/// </summary>
internal abstract record OrderEvent(Guid OrderId, Guid CustomerId, int Version, DateTimeOffset OccurredAt);

internal sealed record OrderPlaced(
    Guid OrderId,
    Guid CustomerId,
    string? CustomerEmail,
    OrderStatus Status,
    decimal Total,
    IReadOnlyList<OrderPlacedLineSnapshot> Lines,
    int Version,
    DateTimeOffset OccurredAt) : OrderEvent(OrderId, CustomerId, Version, OccurredAt);

internal sealed record OrderPlacedLineSnapshot(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity, decimal LineTotal);

internal sealed record OrderStatusChanged(
    Guid OrderId,
    Guid CustomerId,
    OrderStatus FromStatus,
    OrderStatus ToStatus,
    Guid ChangedBy,
    int Version,
    DateTimeOffset OccurredAt) : OrderEvent(OrderId, CustomerId, Version, OccurredAt);
