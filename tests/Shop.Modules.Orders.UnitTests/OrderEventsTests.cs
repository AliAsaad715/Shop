using Shop.Modules.Orders.Domain;
using Xunit;

namespace Shop.Modules.Orders.UnitTests;

/// <summary>
/// The order records an event for every change worth publishing, so no code path can place an order or change its
/// status without one (task R4-02).
/// </summary>
public sealed class OrderEventsTests
{
    private static readonly DateTimeOffset _placedAt = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _book = Guid.Parse("019a2180-2000-7000-8000-000000000001");
    private static readonly Guid _lamp = Guid.Parse("019a2180-2000-7000-8000-000000000002");

    [Fact]
    public void PlacingRecordsOrderPlacedWithWhatTheEmailNeeds()
    {
        var customerId = Guid.CreateVersion7();

        var order = Order.Place(customerId, "ada@shop.test", [(_book, "Book", 19.95m, 3), (_lamp, "Lamp", 45.50m, 1)], _placedAt);

        var placed = Assert.IsType<OrderPlaced>(Assert.Single(order.DequeueEvents()));
        Assert.Equal(order.Id, placed.OrderId);
        Assert.Equal(customerId, placed.CustomerId);
        Assert.Equal("ada@shop.test", placed.CustomerEmail);
        Assert.Equal(OrderStatus.Pending, placed.Status);
        Assert.Equal(105.35m, placed.Total);
        Assert.Equal(0, placed.Version);
        Assert.Equal(_placedAt, placed.OccurredAt);
        Assert.Equal(
            [new OrderPlacedLineSnapshot(_book, "Book", 19.95m, 3, 59.85m), new OrderPlacedLineSnapshot(_lamp, "Lamp", 45.50m, 1, 45.50m)],
            placed.Lines);
    }

    [Fact]
    public void EachStatusChangeRecordsOneEventWithTheVersionAfterIt()
    {
        var adminId = Guid.CreateVersion7();
        var order = Order.Place(Guid.CreateVersion7(), "ada@shop.test", [(_book, "Book", 10m, 1)], _placedAt);
        order.DequeueEvents();

        order.MoveTo(OrderStatus.Confirmed, adminId, _placedAt.AddMinutes(1));
        order.MoveTo(OrderStatus.Shipped, adminId, _placedAt.AddMinutes(2));

        Assert.Equal(
            [
                new OrderStatusChanged(order.Id, order.CustomerId, OrderStatus.Pending, OrderStatus.Confirmed, adminId, 1, _placedAt.AddMinutes(1)),
                new OrderStatusChanged(order.Id, order.CustomerId, OrderStatus.Confirmed, OrderStatus.Shipped, adminId, 2, _placedAt.AddMinutes(2)),
            ],
            order.DequeueEvents());
    }

    [Fact]
    public void AnEventKeepsTheValuesFromWhenItHappened()
    {
        var order = Order.Place(Guid.CreateVersion7(), "ada@shop.test", [(_book, "Book", 10m, 1)], _placedAt);

        // Changed again before anything was saved
        order.MoveTo(OrderStatus.Confirmed, Guid.CreateVersion7(), _placedAt.AddMinutes(1));

        var placed = Assert.IsType<OrderPlaced>(order.DequeueEvents()[0]);
        Assert.Equal(OrderStatus.Pending, placed.Status);
        Assert.Equal(0, placed.Version);
    }

    [Fact]
    public void ARefusedStatusChangeRecordsNothing()
    {
        var order = Order.Place(Guid.CreateVersion7(), "ada@shop.test", [(_book, "Book", 10m, 1)], _placedAt);
        order.DequeueEvents();

        Assert.Throws<InvalidOperationException>(() => order.MoveTo(OrderStatus.Delivered, Guid.CreateVersion7(), _placedAt));

        Assert.Empty(order.DequeueEvents());
    }

    [Fact]
    public void EachEventIsHandedOutOnce()
    {
        var order = Order.Place(Guid.CreateVersion7(), "ada@shop.test", [(_book, "Book", 10m, 1)], _placedAt);

        Assert.Single(order.DequeueEvents());
        Assert.Empty(order.DequeueEvents());
    }
}
