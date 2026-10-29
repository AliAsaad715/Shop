namespace Shop.Modules.Orders.Domain;

// One row per status an order has been in; FromStatus is null for the row written when the order is placed
internal sealed class OrderStatusChange
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OrderId { get; set; }

    public OrderStatus? FromStatus { get; set; }

    public OrderStatus ToStatus { get; set; }

    public Guid ChangedBy { get; set; }

    public DateTimeOffset ChangedAt { get; set; }
}
