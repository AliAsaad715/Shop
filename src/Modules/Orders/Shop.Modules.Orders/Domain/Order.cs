namespace Shop.Modules.Orders.Domain;

internal sealed class Order
{
    // Not mapped: EF Core maps properties, not fields. Saving the order moves these into the outbox
    private readonly List<OrderEvent> _events = [];

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

    /// <summary>
    /// A Pending order whose total is computed here from server-side prices, never taken from the client. It records
    /// <see cref="OrderPlaced"/>, which carries the email the confirmation goes to; the order itself doesn't store it.
    /// </summary>
    public static Order Place(
        Guid customerId, string? customerEmail, IEnumerable<(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity)> lines,
        DateTimeOffset now)
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
        order._events.Add(new OrderPlaced(
            order.Id, customerId, customerEmail, order.Status, order.Total,
            [.. order.Lines.Select(line => new OrderPlacedLineSnapshot(line.ProductId, line.ProductName, line.UnitPrice, line.Quantity, line.LineTotal))],
            order.Version, now));

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
        var from = Status;
        Status = next;
        UpdatedAt = now;
        Version++;
        _events.Add(new OrderStatusChanged(Id, CustomerId, from, next, changedBy, Version, now));
    }

    /// <summary>The events recorded since the last call, oldest first; each is returned once.</summary>
    public IReadOnlyList<OrderEvent> DequeueEvents()
    {
        if (_events.Count == 0)
        {
            return [];
        }

        OrderEvent[] events = [.. _events];
        _events.Clear();
        return events;
    }
}
