using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Orders.Data;
using Shop.Modules.Orders.Domain;
using Shop.Modules.Orders.Outbox;
using Xunit;

namespace Shop.Modules.Orders.IntegrationTests;

// No background relay: each test runs the passes itself, so it sees rows exactly as the request's save left them
public sealed class OutboxRelayFixture : OrdersApiFixture
{
    protected override bool RunOutboxRelay => false;
}

[CollectionDefinition("Outbox relay")]
public sealed class OutboxRelayCollection : ICollectionFixture<OutboxRelayFixture>;

/// <summary>
/// The transactional outbox (task R4-02): an order change and its event commit together or not at all, and a relay
/// pass publishes the oldest rows, marks only what RabbitMQ confirmed, and skips rows another relay has locked.
/// </summary>
[Collection("Outbox relay")]
public sealed class OutboxTests(OutboxRelayFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        fixture.Publisher.Reset();
        fixture.Inventory.Reset();
        fixture.SaveFailure.FailNextOrderSave = false;
        await using var scope = fixture.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().OutboxMessages.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PlacingAnOrderSavesItsEventWithItAndPublishesNothingDuringTheRequest()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerA, bookQuantity: 2);

        var row = Assert.Single(await fixture.OutboxRowsForAsync(orderId));
        Assert.Equal("order.placed", row.Type);
        Assert.Null(row.PublishedAt);
        Assert.Equal(0, row.Attempts);
        var json = JsonDocument.Parse(row.Payload).RootElement;
        Assert.Equal(OrdersApiFixture.CustomerA, json.GetProperty("customerId").GetGuid());
        Assert.Equal(OrdersApiFixture.EmailOf(OrdersApiFixture.CustomerA), json.GetProperty("customerEmail").GetString());
        Assert.Equal(39.90m, json.GetProperty("total").GetDecimal());
        var line = Assert.Single(json.GetProperty("lines").EnumerateArray());
        Assert.Equal("Book", line.GetProperty("productName").GetString());
        Assert.Equal(2, line.GetProperty("quantity").GetInt32());

        // The 201 came back before anything reached RabbitMQ
        Assert.Empty(fixture.Publisher.Received);
    }

    [Fact]
    public async Task AnOrderThatFailsToSaveLeavesNoEvent()
    {
        fixture.SaveFailure.FailNextOrderSave = true;

        using var client = fixture.Customer(OrdersApiFixture.CustomerA);
        using var response = await client.PostAsJsonAsync("/api/v1/orders",
            new { lines = new[] { new { productId = FakeProductCatalog.Book.Id, quantity = 1 } } });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await CountRowsAsync());
    }

    [Fact]
    public async Task EveryStatusChangeSavesItsEventWithTheVersionAfterIt()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerA);
        using var admin = fixture.AdminClient();
        foreach (var status in new[] { "Confirmed", "Shipped" })
        {
            using var moved = await admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status });
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        var rows = await fixture.OutboxRowsForAsync(orderId);

        Assert.Equal(["order.placed", "order.status-changed", "order.status-changed"], rows.Select(row => row.Type));
        var changes = rows.Skip(1).Select(row => JsonDocument.Parse(row.Payload).RootElement).ToList();
        Assert.Equal(["Pending", "Confirmed"], changes.Select(change => change.GetProperty("fromStatus").GetString()));
        Assert.Equal(["Confirmed", "Shipped"], changes.Select(change => change.GetProperty("status").GetString()));
        Assert.Equal([1, 2], changes.Select(change => change.GetProperty("version").GetInt32()));
        Assert.All(changes, change => Assert.Equal(OrdersApiFixture.Admin, change.GetProperty("changedBy").GetGuid()));
    }

    [Fact]
    public async Task AStatusChangeThatLosesTheVersionRaceSavesNoEvent()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerA);

        // Two admins load the same version; the second save fails, and its event must go with it
        await using var firstScope = fixture.App.Services.CreateAsyncScope();
        await using var secondScope = fixture.App.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var firstCopy = await first.Orders.Include(order => order.History).SingleAsync(order => order.Id == orderId);
        var secondCopy = await second.Orders.Include(order => order.History).SingleAsync(order => order.Id == orderId);
        firstCopy.MoveTo(OrderStatus.Confirmed, OrdersApiFixture.Admin, DateTimeOffset.UtcNow);
        await first.SaveChangesAsync();
        secondCopy.MoveTo(OrderStatus.Cancelled, OrdersApiFixture.Admin, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        var rows = await fixture.OutboxRowsForAsync(orderId);

        Assert.Equal(["order.placed", "order.status-changed"], rows.Select(row => row.Type));
        Assert.Equal("Confirmed", JsonDocument.Parse(rows[1].Payload).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task APassPublishesTheOldestRowsEachAsItsOwnMessage()
    {
        var orders = await PlaceOrdersAsync(4);
        var rows = await RowsAsync(orders);

        // The batch size is 3 in these tests
        Assert.Equal(new OutboxPass(3, 3), await fixture.RunOutboxPassAsync());

        var received = fixture.Publisher.Received.ToList();
        Assert.Equal(rows.Take(3).Select(row => row.Id), received.Select(message => message.MessageId));
        Assert.All(received.Zip(rows), pair =>
        {
            var (message, row) = pair;
            Assert.Equal("order.placed", message.RoutingKey);
            Assert.Equal(row.Payload, Encoding.UTF8.GetString(message.Body.Span));
            Assert.Equal(row.OccurredAt, message.OccurredAt);
        });
        var after = await RowsAsync(orders);
        Assert.All(after.Take(3), row =>
        {
            Assert.NotNull(row.PublishedAt);
            Assert.Equal(1, row.Attempts);
        });
        Assert.Null(after[3].PublishedAt);

        Assert.Equal(new OutboxPass(1, 1), await fixture.RunOutboxPassAsync());
        Assert.Equal(new OutboxPass(0, 0), await fixture.RunOutboxPassAsync());
    }

    [Fact]
    public async Task ARowRabbitMqDidNotConfirmStaysWithItsErrorAndTheNextPassRetriesIt()
    {
        var orders = await PlaceOrdersAsync(2);
        var refused = (await RowsAsync(orders))[1].Id;
        fixture.Publisher.Failure = message => message.MessageId == refused ? "RabbitMQ refused it (nack)" : null;

        Assert.Equal(new OutboxPass(2, 1), await fixture.RunOutboxPassAsync());

        var failed = (await RowsAsync(orders))[1];
        Assert.Null(failed.PublishedAt);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("RabbitMQ refused it (nack)", failed.LastError);

        fixture.Publisher.Failure = null;
        Assert.Equal(new OutboxPass(1, 1), await fixture.RunOutboxPassAsync());

        var retried = (await RowsAsync(orders))[1];
        Assert.NotNull(retried.PublishedAt);
        Assert.Equal(2, retried.Attempts);
        Assert.Null(retried.LastError);
    }

    [Fact]
    public async Task ASecondRelaySkipsTheRowsTheFirstHasLockedInsteadOfWaitingForThem()
    {
        var orders = await PlaceOrdersAsync(5);
        var rows = await RowsAsync(orders);

        // The first relay locks the 3 oldest rows and then waits inside the publisher, as on a slow broker
        var hold = fixture.Publisher.HoldNextBatch();
        var first = fixture.RunOutboxPassAsync();
        await hold.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A second instance's relay finishes while those locks are still held, with the 2 other rows
        var second = await fixture.RunOutboxPassAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new OutboxPass(2, 2), second);

        hold.Release.SetResult();
        Assert.Equal(new OutboxPass(3, 3), await first);

        // Each row went out once
        Assert.Equal(rows.Select(row => row.Id).Order(), fixture.Publisher.Received.Select(message => message.MessageId).Order());
    }

    [Fact]
    public async Task CleanupDeletesRowsPublishedBeforeTheCutoffAndNeverUnpublishedOnes()
    {
        var now = DateTimeOffset.UtcNow;
        var oldPublished = new OutboxMessage("order.placed", "{}", now.AddDays(-9));
        oldPublished.MarkPublished(now.AddDays(-8));
        var recentPublished = new OutboxMessage("order.placed", "{}", now.AddHours(-2));
        recentPublished.MarkPublished(now.AddHours(-1));
        var oldUnpublished = new OutboxMessage("order.placed", "{}", now.AddDays(-30));
        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            context.OutboxMessages.AddRange(oldPublished, recentPublished, oldUnpublished);
            await context.SaveChangesAsync();
        }

        await using var cleanup = fixture.App.Services.CreateAsyncScope();
        var deleted = await cleanup.ServiceProvider.GetRequiredService<OutboxProcessor>()
            .DeletePublishedBeforeAsync(now.AddDays(-7), CancellationToken.None);

        Assert.Equal(1, deleted);
        var left = await cleanup.ServiceProvider.GetRequiredService<OrdersDbContext>().OutboxMessages
            .Select(message => message.Id).ToListAsync();
        Assert.Equal(new[] { recentPublished.Id, oldUnpublished.Id }.Order(), left.Order());
    }

    private async Task<List<Guid>> PlaceOrdersAsync(int count)
    {
        var orders = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            orders.Add(await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerA));
        }

        return orders;
    }

    // The order.placed row of each order, in the order given
    private async Task<List<OutboxMessage>> RowsAsync(IEnumerable<Guid> orders)
    {
        var rows = new List<OutboxMessage>();
        foreach (var orderId in orders)
        {
            rows.Add((await fixture.OutboxRowsForAsync(orderId)).Single(row => row.Type == "order.placed"));
        }

        return rows;
    }

    private async Task<int> CountRowsAsync()
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().OutboxMessages.CountAsync();
    }
}

/// <summary>The background relay publishes a new order as soon as its save commits, without waiting for a poll.</summary>
[Collection("Orders database")]
public sealed class OutboxWakeUpTests(OrdersApiFixture fixture)
{
    [Fact]
    public async Task TheRelayPublishesANewOrderAtOnce()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerB);

        // The poll interval is an hour here, so only the wake-up from the save can explain a quick publish
        var row = Assert.Single(await fixture.OutboxRowsForAsync(orderId));
        await WaitUntilAsync(() => fixture.Publisher.Received.Any(message => message.MessageId == row.Id), "the relay to publish it");
        await WaitUntilAsync(
            async () => (await fixture.OutboxRowsForAsync(orderId)).Single().PublishedAt is not null, "the relay to mark it published");
    }

    private static Task WaitUntilAsync(Func<bool> condition, string what) => WaitUntilAsync(() => Task.FromResult(condition()), what);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(50);
        }
    }
}
