namespace Shop.Modules.Orders.Domain;

internal sealed class Order
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    // The user ID from the token's sub claim; Orders never reads the Users schema
    public Guid CustomerId { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    public decimal Total { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // Optimistic concurrency token: a save based on an older version fails with 409
    public int Version { get; set; }

    public List<OrderLine> Lines { get; set; } = [];

    public List<OrderStatusChange> History { get; set; } = [];

    /// <summary>A Pending order whose total is computed here from server-side prices, never taken from the client.</summary>
    public static Order Place(Guid customerId, IEnumerable<(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity)> lines, DateTimeOffset now)
    {
        var order = new Order { CustomerId = customerId, CreatedAt = now, UpdatedAt = now };
        foreach (var (productId, productName, unitPrice, quantity) in lines)
        {
            order.Lines.Add(new OrderLine
            {
                OrderId = order.Id,
                ProductId = productId,
                ProductName = productName,
                UnitPrice = unitPrice,
                Quantity = quantity,
                LineTotal = unitPrice * quantity,
            });
        }

        order.Total = order.Lines.Sum(line => line.LineTotal);
        order.History.Add(new OrderStatusChange { OrderId = order.Id, ToStatus = order.Status, ChangedBy = customerId, ChangedAt = now });

        return order;
    }

    public bool CanMoveTo(OrderStatus next) => OrderStatusTransitions.IsAllowed(Status, next);

    public void MoveTo(OrderStatus next, Guid changedBy, DateTimeOffset now)
    {
        if (!CanMoveTo(next))
        {
            throw new InvalidOperationException($"An order in status {Status} can't move to {next}.");
        }

        History.Add(new OrderStatusChange { OrderId = Id, FromStatus = Status, ToStatus = next, ChangedBy = changedBy, ChangedAt = now });
        Status = next;
        UpdatedAt = now;
        Version++;
    }
}
