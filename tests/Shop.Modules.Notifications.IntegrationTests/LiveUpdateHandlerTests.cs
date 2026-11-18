using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Shop.Messaging;
using Shop.Messaging.Consuming;
using Shop.Modules.Notifications.Contracts.Realtime;
using Shop.Modules.Notifications.Realtime;
using Shop.Modules.Orders.Contracts;
using Xunit;

namespace Shop.Modules.Notifications.IntegrationTests;

// No database or broker: exercise the boundary between private broker events and public browser messages.
public sealed class LiveUpdateHandlerTests
{
    private readonly TestTime _time = new();
    private readonly RecordingClients _orders = new();
    private readonly RecordingClients _admins = new();

    [Fact]
    public async Task NewOrdersBecomeDisplayableAdminRowsWithoutEmailOrLines()
    {
        var order = NotificationsFixture.Order(email: null);
        var message = NotificationsFixture.Message(order);
        var handler = Handler();

        Assert.Equal(LiveUpdateOutcome.Done, await handler.HandleAsync(message, default));

        var sent = Assert.Single(_admins.Sent);
        Assert.Equal("group:admins", sent.Recipient);
        Assert.Equal(RealtimeEvents.OrderPlaced, sent.Method);
        var row = Assert.IsType<OrderPlacedNotification>(Assert.Single(sent.Arguments));
        Assert.Equal(message.MessageId, row.MessageId);
        Assert.Equal(order.OrderId, row.OrderId);
        Assert.Equal(order.CustomerId, row.CustomerId);
        Assert.Equal(3, row.ItemCount);
        Assert.Equal(order.Total, row.Total);
        Assert.Equal(order.PlacedAt, row.PlacedAt);
        Assert.Equal("Pending", row.Status);
        Assert.Equal(0, row.Version);
        Assert.Empty(_orders.Sent);

        var json = JsonSerializer.SerializeToElement(row, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(order.OrderId, json.GetProperty("orderId").GetGuid());
        Assert.False(json.TryGetProperty("customerEmail", out _));
        Assert.False(json.TryGetProperty("lines", out _));
    }

    [Fact]
    public async Task StatusUpdatesTargetTheOwnerAndPreserveOutOfOrderVersions()
    {
        var handler = Handler();
        var latest = Change(version: 2);
        var older = latest with { Version = 1, Status = "Confirmed" };
        foreach (var change in new[] { latest, older })
            Assert.Equal(LiveUpdateOutcome.Done, await handler.HandleAsync(Message(change), default));

        Assert.Equal(2, _orders.Sent.Count);
        Assert.All(_orders.Sent, sent =>
        {
            Assert.Equal($"user:{latest.CustomerId:D}", sent.Recipient);
            Assert.Equal(RealtimeEvents.OrderStatusChanged, sent.Method);
        });
        var rows = _orders.Sent.Select(sent => Assert.IsType<OrderStatusChangedNotification>(Assert.Single(sent.Arguments))).ToArray();
        Assert.Equal([2, 1], rows.Select(row => row.Version));
        Assert.Equal(latest.ChangedAt, rows[0].ChangedAt);
        Assert.Equal(latest.FromStatus, rows[0].FromStatus);
        Assert.Empty(_admins.Sent);
    }

    [Fact]
    public async Task DeduplicationIsLocalAndRecordsOnlySuccessfulSends()
    {
        var message = NotificationsFixture.Message(NotificationsFixture.Order());
        var handler = Handler();
        Assert.Equal(LiveUpdateOutcome.Done, await handler.HandleAsync(message, default));
        Assert.Equal(LiveUpdateOutcome.Done, await handler.HandleAsync(message, default));
        Assert.Single(_admins.Sent);

        // Another process has a separate cache and must still push this event to its local connections.
        Assert.Equal(LiveUpdateOutcome.Done, await Handler().HandleAsync(message, default));
        Assert.Equal(2, _admins.Sent.Count);
    }

    [Fact]
    public async Task FailedSendsHaveBoundedRetriesAndTheMessageCanBeSentAgain()
    {
        var handler = Handler();
        var message = NotificationsFixture.Message(NotificationsFixture.Order());
        _admins.Failures = 3;
        Assert.Equal(LiveUpdateOutcome.Restart, await handler.HandleAsync(message, default));
        Assert.Equal(3, _admins.Attempts);
        Assert.Empty(_admins.Sent);

        Assert.Equal(LiveUpdateOutcome.Done, await handler.HandleAsync(message, default));
        Assert.Single(_admins.Sent);
        Assert.Equal(4, _admins.Attempts);
    }

    [Fact]
    public async Task ATransientSendFailureSucceedsWithoutLosingTheMessage()
    {
        _admins.Failures = 1;
        Assert.Equal(LiveUpdateOutcome.Done,
            await Handler().HandleAsync(NotificationsFixture.Message(NotificationsFixture.Order()), default));
        Assert.Equal(2, _admins.Attempts);
        Assert.Single(_admins.Sent);
    }

    [Fact]
    public async Task CancellationDoesNotRecordASuccess()
    {
        var handler = Handler();
        var message = NotificationsFixture.Message(NotificationsFixture.Order());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(message, cancelled.Token));
        Assert.Empty(_admins.Sent);
        Assert.Equal(LiveUpdateOutcome.Done, await handler.HandleAsync(message, default));
        Assert.Single(_admins.Sent);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task UnreadableOrIncompleteEventsAreRejected(string json)
    {
        var message = new ReceivedMessage(Guid.NewGuid(), ShopTopology.RoutingKeys.OrderPlaced, Encoding.UTF8.GetBytes(json), 0);
        Assert.Equal(LiveUpdateOutcome.Reject, await Handler().HandleAsync(message, default));
        Assert.Empty(_admins.Sent);
    }

    [Fact]
    public async Task InvalidRecipientsVersionsAndCountsAreRejected()
    {
        var order = NotificationsFixture.Order();
        var handler = Handler();
        foreach (var invalid in new[]
        {
            order with { CustomerId = Guid.Empty }, order with { OrderId = Guid.Empty }, order with { Version = -1 },
            order with { Lines = null! }, order with { Lines = [null!] },
            order with { Lines = [order.Lines[0] with { Quantity = 0 }] },
            order with { Lines = [order.Lines[0] with { Quantity = int.MaxValue }, order.Lines[1]] },
        })
            Assert.Equal(LiveUpdateOutcome.Reject, await handler.HandleAsync(NotificationsFixture.Message(invalid), default));

        Assert.Equal(LiveUpdateOutcome.Reject,
            await handler.HandleAsync(NotificationsFixture.Message(order) with { MessageId = null }, default));
        Assert.Equal(LiveUpdateOutcome.Reject,
            await handler.HandleAsync(Message(Change() with { Version = 0 }), default));
        Assert.Empty(_orders.Sent);
        Assert.Empty(_admins.Sent);
    }

    [Fact]
    public async Task FutureEventTypesAreIgnoredAndResyncContainsNoOrderData()
    {
        var handler = Handler();
        Assert.Equal(LiveUpdateOutcome.Done,
            await handler.HandleAsync(new ReceivedMessage(null, "order.future-event", "invalid"u8.ToArray(), 0), default));
        await handler.ResynchronizeAsync(default);
        foreach (var clients in new[] { _orders, _admins })
        {
            var sent = Assert.Single(clients.Sent);
            Assert.Equal("all", sent.Recipient);
            Assert.Equal(RealtimeEvents.ResyncRequired, sent.Method);
            Assert.Empty(sent.Arguments);
        }
    }

    [Fact]
    public void DeduplicationExpiresAndHasABoundedCapacity()
    {
        var cache = new RecentRealtimeMessages(_time);
        var first = Guid.NewGuid();
        cache.Record(first);
        Assert.True(cache.Contains(first));
        _time.Advance(RecentRealtimeMessages.Lifetime);
        Assert.False(cache.Contains(first));

        cache.Record(first);
        var last = Guid.Empty;
        for (var i = 0; i < RecentRealtimeMessages.Capacity; i++) cache.Record(last = Guid.NewGuid());
        Assert.False(cache.Contains(first));
        Assert.True(cache.Contains(last));
    }

    private LiveUpdateHandler Handler() => new(
        new RecordingHub<OrdersHub>(_orders), new RecordingHub<AdminHub>(_admins),
        new RecentRealtimeMessages(_time), NullLogger<LiveUpdateHandler>.Instance);

    private static OrderStatusChangedEvent Change(int version = 1) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Pending", "Confirmed", version, Guid.NewGuid(), DateTimeOffset.UtcNow);

    private static ReceivedMessage Message(OrderStatusChangedEvent change) => new(
        Guid.NewGuid(), ShopTopology.RoutingKeys.OrderStatusChanged,
        JsonSerializer.SerializeToUtf8Bytes(change, new JsonSerializerOptions(JsonSerializerDefaults.Web)), 0);

    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class RecordingHub<THub>(IHubClients clients) : IHubContext<THub> where THub : Hub
    {
        public IHubClients Clients => clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed record Sent(string Recipient, string Method, object?[] Arguments);

    private sealed class RecordingClients : IHubClients
    {
        public List<Sent> Sent { get; } = [];
        public int Failures { get; set; }
        public int Attempts { get; private set; }
        public IClientProxy All => new Proxy(this, "all");
        public IClientProxy Group(string name) => new Proxy(this, $"group:{name}");
        public IClientProxy User(string id) => new Proxy(this, $"user:{id}");
        public IClientProxy AllExcept(IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy Client(string id) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string name, IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> names) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> ids) => throw new NotSupportedException();

        private sealed class Proxy(RecordingClients owner, string recipient) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                owner.Attempts++;
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.Failures-- > 0) throw new IOException("Simulated SignalR send failure");
                owner.Sent.Add(new Sent(recipient, method, args));
                return Task.CompletedTask;
            }
        }
    }
}
