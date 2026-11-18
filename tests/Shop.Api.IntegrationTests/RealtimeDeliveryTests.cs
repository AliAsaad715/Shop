using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shop.Messaging;
using Shop.Messaging.Publishing;
using Shop.Modules.Notifications.Contracts.Realtime;
using Shop.Modules.Notifications.Realtime;
using Shop.Modules.Orders.Contracts;
using Shop.SharedKernel.Auth;
using Xunit;

namespace Shop.Api.IntegrationTests;

[Collection(RealtimeCollection.Name)]
public sealed class RealtimeDeliveryTests(RealtimeFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<SignalRApi> _apis = [];

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach (var api in _apis) await api.DisposeAsync();
    }

    [Fact]
    public async Task RealEventsReachAdminsAndAllOwnerConnectionsWithoutLeakingToOtherCustomers()
    {
        var (api, _) = await ApiAsync();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        await using var a1 = api.Connection("/hubs/orders", api.Token(owner));
        await using var a2 = api.Connection("/hubs/orders", api.Token(owner));
        await using var b = api.Connection("/hubs/orders", api.Token(other));
        await using var admin = api.Connection("/hubs/admin", api.Token(Guid.NewGuid(), Roles.Admin));
        var a1Inbox = new Inbox(a1);
        var a2Inbox = new Inbox(a2);
        var bInbox = new Inbox(b);
        var adminInbox = new Inbox(admin);
        using var timeout = Timeout();
        foreach (var client in new[] { a1, a2, b, admin }) await api.StartAsync(client, timeout.Token);

        var placed = Placed(owner);
        await PublishAsync(api, placed);
        var ownerChange = Changed(placed.OrderId, owner);
        var otherChange = Changed(Guid.NewGuid(), other);
        var ownerBarrier = Changed(Guid.NewGuid(), owner);
        var otherBarrier = Changed(Guid.NewGuid(), other);
        var adminBarrier = Placed(other);
        foreach (var change in new[] { ownerChange, otherChange, ownerBarrier, otherBarrier }) await PublishAsync(api, change);
        await PublishAsync(api, adminBarrier);

        foreach (var inbox in new[] { a1Inbox, a2Inbox })
        {
            Assert.Equal(ownerChange.OrderId, (await inbox.DataAsync(RealtimeEvents.OrderStatusChanged, timeout.Token)).GetProperty("orderId").GetGuid());
            Assert.Equal(ownerBarrier.OrderId, (await inbox.DataAsync(RealtimeEvents.OrderStatusChanged, timeout.Token)).GetProperty("orderId").GetGuid());
        }
        Assert.Equal(otherChange.OrderId, (await bInbox.DataAsync(RealtimeEvents.OrderStatusChanged, timeout.Token)).GetProperty("orderId").GetGuid());
        Assert.Equal(otherBarrier.OrderId, (await bInbox.DataAsync(RealtimeEvents.OrderStatusChanged, timeout.Token)).GetProperty("orderId").GetGuid());
        var row = await adminInbox.DataAsync(RealtimeEvents.OrderPlaced, timeout.Token);
        Assert.Equal(placed.OrderId, row.GetProperty("orderId").GetGuid());
        Assert.Equal(3, row.GetProperty("itemCount").GetInt32());
        Assert.False(row.TryGetProperty("customerEmail", out _));
        Assert.False(row.TryGetProperty("lines", out _));
        Assert.Equal(adminBarrier.OrderId, (await adminInbox.DataAsync(RealtimeEvents.OrderPlaced, timeout.Token)).GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task EachInstanceHasItsOwnExclusiveQueueAndReceivesTheSameMessage()
    {
        var (first, _) = await ApiAsync();
        var (second, _) = await ApiAsync();
        var firstState = State(first);
        var secondState = State(second);
        Assert.NotEqual(firstState.QueueName, secondState.QueueName);

        foreach (var api in new[] { first, second })
        {
            var state = State(api);
            var queue = await fixture.JsonAsync($"queues/%2F/{state.QueueName}");
            Assert.Equal("classic", queue.GetProperty("type").GetString());
            Assert.False(queue.GetProperty("durable").GetBoolean());
            Assert.True(queue.GetProperty("exclusive").GetBoolean());
            Assert.True(queue.GetProperty("auto_delete").GetBoolean());
            Assert.Equal(1u, await ConsumerCountAsync(api));
            var bindings = await fixture.JsonAsync($"bindings/%2F/e/{ShopTopology.EventsExchange}/q/{state.QueueName}");
            Assert.Equal("order.*", Assert.Single(bindings.EnumerateArray()).GetProperty("routing_key").GetString());
        }

        await using var adminA = first.Connection("/hubs/admin", first.Token(Guid.NewGuid(), Roles.Admin));
        await using var adminB = second.Connection("/hubs/admin", second.Token(Guid.NewGuid(), Roles.Admin));
        var inboxA = new Inbox(adminA);
        var inboxB = new Inbox(adminB);
        using var timeout = Timeout();
        await first.StartAsync(adminA, timeout.Token);
        await second.StartAsync(adminB, timeout.Token);
        var messageId = await PublishAsync(first, Placed(Guid.NewGuid()));
        foreach (var inbox in new[] { inboxA, inboxB })
            Assert.Equal(messageId, (await inbox.DataAsync(RealtimeEvents.OrderPlaced, timeout.Token)).GetProperty("messageId").GetGuid());
    }

    [Fact]
    public async Task DuplicateAndMalformedEventsDoNotDuplicateUpdatesOrBlockLaterMessages()
    {
        var (api, _) = await ApiAsync();
        await using var admin = api.Connection("/hubs/admin", api.Token(Guid.NewGuid(), Roles.Admin));
        var inbox = new Inbox(admin);
        using var timeout = Timeout();
        await api.StartAsync(admin, timeout.Token);
        var order = Placed(Guid.NewGuid());
        var id = await PublishAsync(api, order);
        await PublishAsync(api, order, id);
        await PublishRawAsync(api, new OutgoingMessage(Guid.NewGuid(), ShopTopology.RoutingKeys.OrderPlaced, "not-json"u8.ToArray(), DateTimeOffset.UtcNow));
        await PublishRawAsync(api, new OutgoingMessage(Guid.NewGuid(), "order.future-event", "{}"u8.ToArray(), DateTimeOffset.UtcNow));
        var barrier = Placed(Guid.NewGuid());
        await PublishAsync(api, barrier);
        Assert.Equal(order.OrderId, (await inbox.DataAsync(RealtimeEvents.OrderPlaced, timeout.Token)).GetProperty("orderId").GetGuid());
        Assert.Equal(barrier.OrderId, (await inbox.DataAsync(RealtimeEvents.OrderPlaced, timeout.Token)).GetProperty("orderId").GetGuid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostConnectionOrDeletedQueueRecoversAndRequestsASnapshotWithoutReconnectingSignalR(bool deleteQueue)
    {
        var (api, clientName) = await ApiAsync();
        var owner = Guid.NewGuid();
        await using var customer = api.Connection("/hubs/orders", api.Token(owner));
        await using var admin = api.Connection("/hubs/admin", api.Token(Guid.NewGuid(), Roles.Admin));
        var customerInbox = new Inbox(customer);
        var adminInbox = new Inbox(admin);
        using var timeout = Timeout(40);
        await api.StartAsync(customer, timeout.Token);
        await api.StartAsync(admin, timeout.Token);
        var connectionId = customer.ConnectionId;
        var oldQueue = State(api).QueueName!;

        // Establish an ordering barrier after the initial ResyncRequired, before disrupting RabbitMQ.
        var before = Changed(Guid.NewGuid(), owner);
        await PublishAsync(api, before);
        await customerInbox.DataAsync(RealtimeEvents.OrderStatusChanged, timeout.Token);
        await PublishAsync(api, Placed(owner));
        await adminInbox.DataAsync(RealtimeEvents.OrderPlaced, timeout.Token);

        if (deleteQueue)
        {
            // Only the declaring connection can operate on an exclusive queue, including deleting it.
            var ownerConnection = await api.Services.GetRequiredService<RabbitMqConnection>().GetAsync(timeout.Token);
            await using var probe = await ownerConnection.CreateChannelAsync(cancellationToken: timeout.Token);
            await probe.QueueDeleteAsync(oldQueue, ifUnused: false, ifEmpty: false, cancellationToken: timeout.Token);
        }
        else await fixture.CloseConnectionAsync(clientName);
        await customerInbox.ResyncAsync(timeout.Token);
        await adminInbox.ResyncAsync(timeout.Token);
        await ReadyAsync(api);
        Assert.NotEqual(oldQueue, State(api).QueueName);
        Assert.Equal(HubConnectionState.Connected, customer.State);
        Assert.Equal(connectionId, customer.ConnectionId);
        Assert.Equal(1u, await ConsumerCountAsync(api));
        Assert.Equal(HealthStatus.Healthy, await HealthAsync(api));

        var after = Changed(Guid.NewGuid(), owner, version: 2);
        await PublishAsync(api, after);
        var received = await customerInbox.DataAsync(RealtimeEvents.OrderStatusChanged, timeout.Token);
        Assert.Equal(after.OrderId, received.GetProperty("orderId").GetGuid());
        Assert.Equal(2, received.GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryNeverLeavesALiveQueueWithoutAConsumer(bool replaceFirst)
    {
        // A queue left behind keeps a copy of every order event until the API stops, and nothing reads it. Three
        // losses in a row: the connection drops, or failed sends make the service replace its subscription; then the
        // connection drops twice more, and each time RabbitMQ must hold exactly one live queue for this API
        var (api, clientName) = await ApiAsync();
        await using var admin = api.Connection("/hubs/admin", api.Token(Guid.NewGuid(), Roles.Admin));
        var inbox = new Inbox(admin);
        using var timeout = Timeout(90);
        await api.StartAsync(admin, timeout.Token);
        await DeliveredAsync(api, inbox, timeout.Token);

        for (var loss = 1; loss <= 3; loss++)
        {
            if (loss == 1 && replaceFirst)
            {
                api.Services.GetRequiredService<ScriptedAdminLifetimeManager>().Failures = 3;
                await PublishAsync(api, Placed(Guid.NewGuid()));
            }
            else await fixture.CloseConnectionAsync(clientName);
            await inbox.ResyncAsync(timeout.Token);
            await ReadyAsync(api);
            await DeliveredAsync(api, inbox, timeout.Token);

            Assert.Equal([State(api).QueueName!], await LiveQueuesAsync());
        }
    }

    [Fact]
    public async Task ExhaustedSignalRSendsReplaceTheSubscriptionAndAllowTheSameMessageToSucceedLater()
    {
        var (api, _) = await ApiAsync();
        await using var admin = api.Connection("/hubs/admin", api.Token(Guid.NewGuid(), Roles.Admin));
        var inbox = new Inbox(admin);
        using var timeout = Timeout();
        await api.StartAsync(admin, timeout.Token);
        await PublishAsync(api, Placed(Guid.NewGuid()));
        await inbox.DataAsync(RealtimeEvents.OrderPlaced, timeout.Token);

        var script = api.Services.GetRequiredService<ScriptedAdminLifetimeManager>();
        var oldQueue = State(api).QueueName;
        script.Failures = 3;
        var order = Placed(Guid.NewGuid());
        var messageId = await PublishAsync(api, order);
        await inbox.ResyncAsync(timeout.Token);
        await ReadyAsync(api);
        Assert.NotEqual(oldQueue, State(api).QueueName);
        await PublishAsync(api, order, messageId);
        Assert.Equal(order.OrderId, (await inbox.DataAsync(RealtimeEvents.OrderPlaced, timeout.Token)).GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task GracefulStopDrainsTheCurrentSendAndRemovesOnlyItsOwnQueue()
    {
        var (first, _) = await ApiAsync();
        var (second, _) = await ApiAsync();
        var script = first.Services.GetRequiredService<ScriptedAdminLifetimeManager>();
        script.Hold = true;
        var oldQueue = State(first).QueueName!;
        var otherQueue = State(second).QueueName!;
        using var timeout = Timeout();
        await PublishAsync(first, Placed(Guid.NewGuid()));
        await script.Started.Task.WaitAsync(timeout.Token);

        var consumer = first.Services.GetServices<IHostedService>().OfType<LiveUpdateConsumer>().Single();
        var stop = consumer.StopAsync(timeout.Token);
        await RealtimeFixture.WaitAsync(() => !State(first).IsReady, "the subscription to stop");
        Assert.False(stop.IsCompleted);
        script.Release();
        await stop.WaitAsync(timeout.Token);
        await RealtimeFixture.WaitAsync(async () => !await fixture.QueueExistsAsync(oldQueue), "the stopped instance's queue to disappear");
        Assert.True(await fixture.QueueExistsAsync(otherQueue));
        Assert.True(State(second).IsReady);

        await using var admin = second.Connection("/hubs/admin", second.Token(Guid.NewGuid(), Roles.Admin));
        var inbox = new Inbox(admin);
        await second.StartAsync(admin, timeout.Token);
        var next = Placed(Guid.NewGuid());
        await PublishAsync(second, next);
        Assert.Equal(next.OrderId, (await inbox.DataAsync(RealtimeEvents.OrderPlaced, timeout.Token)).GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task ManualAcknowledgementsBoundThePrefetchWhileOneSendIsHeld()
    {
        var (api, _) = await ApiAsync();
        await using var admin = api.Connection("/hubs/admin", api.Token(Guid.NewGuid(), Roles.Admin));
        var inbox = new Inbox(admin);
        using var timeout = Timeout();
        await api.StartAsync(admin, timeout.Token);
        var script = api.Services.GetRequiredService<ScriptedAdminLifetimeManager>();
        script.Hold = true;
        var ids = new List<Guid>();
        ids.Add(await PublishAsync(api, Placed(Guid.NewGuid())));
        await script.Started.Task.WaitAsync(timeout.Token);
        for (var i = 1; i < 7; i++) ids.Add(await PublishAsync(api, Placed(Guid.NewGuid())));

        await RealtimeFixture.WaitAsync(async () =>
        {
            var connection = await api.Services.GetRequiredService<RabbitMqConnection>().GetAsync(timeout.Token);
            await using var probe = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
            return (await probe.QueueDeclarePassiveAsync(State(api).QueueName!, timeout.Token)).MessageCount == 2;
        }, "five unacknowledged deliveries and two ready messages");
        Assert.Equal(1, script.Attempts);
        script.Release();
        foreach (var id in ids)
            Assert.Equal(id, (await inbox.DataAsync(RealtimeEvents.OrderPlaced, timeout.Token)).GetProperty("messageId").GetGuid());
    }

    [Fact]
    public async Task ShutdownCancelsAnInFlightSendWhenTheHostsRemainingBudgetExpires()
    {
        var (api, _) = await ApiAsync();
        var script = api.Services.GetRequiredService<ScriptedAdminLifetimeManager>();
        script.Hold = true;
        var queue = State(api).QueueName!;
        using var timeout = Timeout();
        await PublishAsync(api, Placed(Guid.NewGuid()));
        await script.Started.Task.WaitAsync(timeout.Token);
        var consumer = api.Services.GetServices<IHostedService>().OfType<LiveUpdateConsumer>().Single();
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try { await consumer.StopAsync(budget.Token).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
        await script.Cancelled.Task.WaitAsync(timeout.Token);
        await RealtimeFixture.WaitAsync(async () => !await fixture.QueueExistsAsync(queue), "the cancelled subscription to close");
        Assert.False(State(api).IsReady);
    }

    [Fact]
    public async Task ApiStartsAndServesAuthenticatedConnectionsWhileTheBrokerIsUnavailable()
    {
        var settings = fixture.Settings;
        settings["RabbitMq:Host"] = "127.0.0.1";
        settings["RabbitMq:Port"] = "1";
        var api = new SignalRApi(settings);
        _apis.Add(api);
        using var timeout = Timeout(10);
        await api.InitializeAsync().WaitAsync(timeout.Token);
        Assert.Equal(HealthStatus.Degraded, await HealthAsync(api));
        await using var customer = api.Connection("/hubs/orders", api.Token(Guid.NewGuid()));
        await api.StartAsync(customer, timeout.Token);
        Assert.Equal(HubConnectionState.Connected, customer.State);
    }

    private async Task<(SignalRApi Api, string ClientName)> ApiAsync()
    {
        var name = $"realtime-tests-{Guid.NewGuid():N}";
        var api = new SignalRApi(fixture.Settings, name, services =>
        {
            services.AddSingleton<ScriptedAdminLifetimeManager>();
            services.AddSingleton<HubLifetimeManager<AdminHub>>(provider => provider.GetRequiredService<ScriptedAdminLifetimeManager>());
        });
        _apis.Add(api);
        await api.InitializeAsync();
        await ReadyAsync(api);
        return (api, name);
    }

    private static LiveUpdateState State(SignalRApi api) => api.Services.GetRequiredService<LiveUpdateState>();
    private static async Task<uint> ConsumerCountAsync(SignalRApi api)
    {
        var connection = await api.Services.GetRequiredService<RabbitMqConnection>().GetAsync(default);
        await using var probe = await connection.CreateChannelAsync();
        return (await probe.QueueDeclarePassiveAsync(State(api).QueueName!)).ConsumerCount;
    }
    private static Task ReadyAsync(SignalRApi api) => RealtimeFixture.WaitAsync(() => State(api).IsReady, "live subscription to be ready");

    // Publishes a new order and waits for the admin to get it, so everything sent before it has arrived too
    private static async Task DeliveredAsync(SignalRApi api, Inbox inbox, CancellationToken ct)
    {
        var order = Placed(Guid.NewGuid());
        await PublishAsync(api, order);
        Assert.Equal(order.OrderId, (await inbox.DataAsync(RealtimeEvents.OrderPlaced, ct)).GetProperty("orderId").GetGuid());
    }

    // Every live queue in the collection's broker. Its tests run one at a time and dispose their APIs, so these all
    // belong to the running test's APIs; the broker's queue list doesn't say which connection owns a queue
    private async Task<string[]> LiveQueuesAsync() =>
        [.. (await fixture.JsonAsync("queues/%2F")).EnumerateArray()
            .Where(queue => queue.GetProperty("exclusive").GetBoolean())
            .Select(queue => queue.GetProperty("name").GetString()!)
            .Order()];

    private static async Task<HealthStatus> HealthAsync(SignalRApi api) =>
        (await api.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Status;
    private static CancellationTokenSource Timeout(int seconds = 30) => new(TimeSpan.FromSeconds(seconds));

    private static OrderPlacedEvent Placed(Guid owner) => new(Guid.NewGuid(), owner, "private@shop.test", "Pending", 30m,
        [new OrderPlacedLine(Guid.NewGuid(), "Product", 10m, 3, 30m)], 0, DateTimeOffset.UtcNow);
    private static OrderStatusChangedEvent Changed(Guid order, Guid owner, int version = 1) =>
        new(order, owner, "Pending", "Confirmed", version, Guid.NewGuid(), DateTimeOffset.UtcNow);

    private static async Task<Guid> PublishAsync<T>(SignalRApi api, T body, Guid? messageId = null)
    {
        var id = messageId ?? Guid.NewGuid();
        await PublishRawAsync(api, new OutgoingMessage(id,
            body is OrderPlacedEvent ? ShopTopology.RoutingKeys.OrderPlaced : ShopTopology.RoutingKeys.OrderStatusChanged,
            JsonSerializer.SerializeToUtf8Bytes(body, Json), DateTimeOffset.UtcNow));
        return id;
    }

    private static async Task PublishRawAsync(SignalRApi api, OutgoingMessage message)
    {
        var result = Assert.Single(await api.Services.GetRequiredService<IEventPublisher>().PublishAsync([message], default));
        Assert.True(result.Confirmed, result.Failure);
    }

    private sealed class Inbox
    {
        private readonly Channel<(string Method, JsonElement Payload)> _messages = Channel.CreateUnbounded<(string, JsonElement)>();

        public Inbox(HubConnection connection)
        {
            foreach (var method in new[] { RealtimeEvents.OrderPlaced, RealtimeEvents.OrderStatusChanged })
                connection.On<JsonElement>(method, payload => _messages.Writer.TryWrite((method, payload)));
            connection.On(RealtimeEvents.ResyncRequired, () => { _messages.Writer.TryWrite((RealtimeEvents.ResyncRequired, default)); });
        }

        public async Task<JsonElement> DataAsync(string method, CancellationToken ct)
        {
            var message = await _messages.Reader.ReadAsync(ct);
            while (message.Method == RealtimeEvents.ResyncRequired) message = await _messages.Reader.ReadAsync(ct);
            Assert.Equal(method, message.Method);
            return message.Payload;
        }

        public async Task ResyncAsync(CancellationToken ct)
        {
            var message = await _messages.Reader.ReadAsync(ct);
            Assert.Equal(RealtimeEvents.ResyncRequired, message.Method);
        }
    }

    private sealed class ScriptedAdminLifetimeManager(ILogger<DefaultHubLifetimeManager<AdminHub>> logger)
        : DefaultHubLifetimeManager<AdminHub>(logger)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts { get; private set; }
        public int Failures { get; set; }
        public bool Hold { get; set; }

        public void Release() { Hold = false; _released.TrySetResult(); }

        public override async Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            if (methodName == RealtimeEvents.OrderPlaced)
            {
                Attempts++;
                if (Hold)
                {
                    Started.TrySetResult();
                    try { await _released.Task.WaitAsync(cancellationToken); }
                    catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
                }
                if (Failures-- > 0) throw new IOException("Simulated send failure");
            }
            await base.SendGroupAsync(groupName, methodName, args, cancellationToken);
        }
    }
}
