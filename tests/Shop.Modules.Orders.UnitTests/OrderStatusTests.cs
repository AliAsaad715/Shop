using Shop.Modules.Orders.Domain;
using Xunit;

namespace Shop.Modules.Orders.UnitTests;

public sealed class OrderStatusTests
{
    private static readonly DateTimeOffset _placedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, string> Allowed => new()
    {
        { "Pending", "Confirmed" },
        { "Confirmed", "Shipped" },
        { "Shipped", "Delivered" },
        { "Pending", "Cancelled" },
        { "Confirmed", "Cancelled" },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void AllowedTransitionsAreAccepted(string from, string to) =>
        Assert.True(OrderStatusTransitions.IsAllowed(Enum.Parse<OrderStatus>(from), Enum.Parse<OrderStatus>(to)));

    [Fact]
    public void EveryOtherTransitionIsRejected()
    {
        var allowed = Allowed.Select(row => ((string)row[0], (string)row[1])).ToHashSet();
        foreach (var from in Enum.GetValues<OrderStatus>())
        {
            foreach (var to in Enum.GetValues<OrderStatus>())
            {
                Assert.Equal(allowed.Contains((from.ToString(), to.ToString())), OrderStatusTransitions.IsAllowed(from, to));
            }
        }
    }

    [Fact]
    public void PlacedOrderIsPendingWithServerComputedTotalsAndOneHistoryRow()
    {
        var customerId = Guid.CreateVersion7();
        var order = Order.Place(customerId, [(Guid.CreateVersion7(), "Book", 19.95m, 3), (Guid.CreateVersion7(), "Lamp", 0.10m, 1)], _placedAt);

        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(customerId, order.CustomerId);
        Assert.Equal(59.95m, order.Total);
        Assert.Equal(new[] { 59.85m, 0.10m }, order.Lines.Select(line => line.LineTotal));
        Assert.All(order.Lines, line => Assert.Equal(order.Id, line.OrderId));
        var created = Assert.Single(order.History);
        Assert.Null(created.FromStatus);
        Assert.Equal(OrderStatus.Pending, created.ToStatus);
        Assert.Equal(customerId, created.ChangedBy);
    }

    [Fact]
    public void MoveToRecordsHistoryAndBumpsTheVersion()
    {
        var adminId = Guid.CreateVersion7();
        var order = Order.Place(Guid.CreateVersion7(), [(Guid.CreateVersion7(), "Book", 10m, 1)], _placedAt);

        order.MoveTo(OrderStatus.Confirmed, adminId, _placedAt.AddMinutes(5));

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(1, order.Version);
        Assert.Equal(_placedAt.AddMinutes(5), order.UpdatedAt);
        var change = order.History[^1];
        Assert.Equal(OrderStatus.Pending, change.FromStatus);
        Assert.Equal(OrderStatus.Confirmed, change.ToStatus);
        Assert.Equal(adminId, change.ChangedBy);
    }

    [Fact]
    public void InvalidMoveThrowsAndChangesNothing()
    {
        var order = Order.Place(Guid.CreateVersion7(), [(Guid.CreateVersion7(), "Book", 10m, 1)], _placedAt);

        var error = Assert.Throws<InvalidOperationException>(() => order.MoveTo(OrderStatus.Delivered, Guid.CreateVersion7(), _placedAt));

        Assert.Equal("An order in status Pending can't move to Delivered.", error.Message);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(0, order.Version);
        Assert.Single(order.History);
    }
}
