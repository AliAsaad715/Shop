using System.Text.Json;
using Shop.Modules.Orders.Domain;
using Shop.Modules.Orders.Outbox;
using Xunit;

namespace Shop.Modules.Orders.UnitTests;

/// <summary>
/// What an outbox row holds: the routing key, and the public event from Orders.Contracts as camelCase JSON, which is
/// exactly what consumers receive (task R4-02).
/// </summary>
public sealed class OutboxMessageTests
{
    private static readonly DateTimeOffset _placedAt = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _book = Guid.Parse("019a2180-2000-7000-8000-000000000001");

    [Fact]
    public void OrderPlacedBecomesAnOrderPlacedRowWithTheContractAsJson()
    {
        var customerId = Guid.CreateVersion7();
        var order = Order.Place(customerId, "ada@shop.test", [(_book, "Book", 19.95m, 3)], _placedAt);

        var row = OrderEventMessages.ToOutboxMessage(order.DequeueEvents()[0]);

        Assert.Equal("order.placed", row.Type);
        Assert.Equal(_placedAt, row.OccurredAt);
        Assert.Null(row.PublishedAt);
        Assert.Equal(0, row.Attempts);
        var json = JsonDocument.Parse(row.Payload).RootElement;
        Assert.Equal(order.Id, json.GetProperty("orderId").GetGuid());
        Assert.Equal(customerId, json.GetProperty("customerId").GetGuid());
        Assert.Equal("ada@shop.test", json.GetProperty("customerEmail").GetString());
        Assert.Equal("Pending", json.GetProperty("status").GetString());
        Assert.Equal(59.85m, json.GetProperty("total").GetDecimal());
        Assert.Equal(0, json.GetProperty("version").GetInt32());
        Assert.Equal(_placedAt, json.GetProperty("placedAt").GetDateTimeOffset());
        var line = Assert.Single(json.GetProperty("lines").EnumerateArray());
        Assert.Equal(_book, line.GetProperty("productId").GetGuid());
        Assert.Equal("Book", line.GetProperty("productName").GetString());
        Assert.Equal(19.95m, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(3, line.GetProperty("quantity").GetInt32());
        Assert.Equal(59.85m, line.GetProperty("lineTotal").GetDecimal());
    }

    [Fact]
    public void StatusChangeBecomesAStatusChangedRowWithTheNewStatusAndVersion()
    {
        var adminId = Guid.CreateVersion7();
        var order = Order.Place(Guid.CreateVersion7(), "ada@shop.test", [(_book, "Book", 10m, 1)], _placedAt);
        order.DequeueEvents();
        order.MoveTo(OrderStatus.Confirmed, adminId, _placedAt.AddMinutes(1));

        var row = OrderEventMessages.ToOutboxMessage(order.DequeueEvents()[0]);

        Assert.Equal("order.status-changed", row.Type);
        var json = JsonDocument.Parse(row.Payload).RootElement;
        Assert.Equal(order.Id, json.GetProperty("orderId").GetGuid());
        Assert.Equal(order.CustomerId, json.GetProperty("customerId").GetGuid());
        Assert.Equal("Pending", json.GetProperty("fromStatus").GetString());
        Assert.Equal("Confirmed", json.GetProperty("status").GetString());
        Assert.Equal(1, json.GetProperty("version").GetInt32());
        Assert.Equal(adminId, json.GetProperty("changedBy").GetGuid());
        Assert.Equal(_placedAt.AddMinutes(1), json.GetProperty("changedAt").GetDateTimeOffset());
    }

    [Fact]
    public void ATokenWithoutAnEmailStillPlacesTheOrder()
    {
        var order = Order.Place(Guid.CreateVersion7(), null, [(_book, "Book", 10m, 1)], _placedAt);

        var json = JsonDocument.Parse(OrderEventMessages.ToOutboxMessage(order.DequeueEvents()[0]).Payload).RootElement;

        // The email worker parks such a message in the DLQ; the order itself is fine
        Assert.Equal(JsonValueKind.Null, json.GetProperty("customerEmail").ValueKind);
    }

    [Fact]
    public void EveryAttemptCountsAndOnlyTheLastErrorIsKept()
    {
        var row = new OutboxMessage("order.placed", "{}", _placedAt);

        row.MarkFailed("RabbitMQ can't be reached");
        row.MarkFailed(new string('x', 5000));

        Assert.Equal(2, row.Attempts);
        Assert.Equal(OutboxMessage.MaxErrorLength, row.LastError!.Length);
        Assert.Null(row.PublishedAt);

        row.MarkPublished(_placedAt.AddSeconds(3));

        Assert.Equal(3, row.Attempts);
        Assert.Equal(_placedAt.AddSeconds(3), row.PublishedAt);
        Assert.Null(row.LastError);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(100, 30)]
    public void TheRelayWaitsLongerAfterEachFailedPassUpTo30Seconds(int failedPasses, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), OutboxRelay.RetryDelay(failedPasses));
}
