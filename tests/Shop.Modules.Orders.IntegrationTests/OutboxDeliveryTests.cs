using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Shop.Messaging;
using Shop.Modules.Orders.Data;
using Shop.Modules.Orders.Domain;
using Shop.Modules.Orders.Outbox;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Shop.Modules.Orders.IntegrationTests;

// Uses the real Orders transaction, outbox processor and publisher, with a disposable database and broker.
public sealed class OutboxDeliveryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4.3.7-management-alpine")
        .WithUsername("shop").WithPassword("shop").Build();
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _broker.StartAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = _postgres.GetConnectionString(),
            ["ReadRouting:Enabled"] = "false",
            ["RabbitMq:Host"] = _broker.Hostname,
            ["RabbitMq:Port"] = _broker.GetMappedPublicPort(5672).ToString(),
            ["RabbitMq:UserName"] = "shop",
            ["RabbitMq:Password"] = "shop",
        }).Build());
        services.AddLogging();
        services.AddOrdersModule();
        services.AddShopMessaging("outbox-delivery-test");
        // No host is started: only the test runs relay passes, so background timing cannot hide a failed pass.
        _services = services.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        await _broker.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task MissingEmailQueueAndBrokerOutageLeaveOrdersSavedAndOutboxEventsRetryable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var connection = await _services.GetRequiredService<RabbitMqConnection>().GetAsync(timeout.Token);
        await using var live = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
        var liveQueue = await ShopTopology.DeclareLiveQueueAsync(live, timeout.Token);
        await live.QueueDeleteAsync(ShopTopology.EmailQueue, ifUnused: false, ifEmpty: false, cancellationToken: timeout.Token);

        var first = await PlaceAsync(timeout.Token);
        Assert.Equal(new OutboxPass(1, 0), await PassAsync(timeout.Token));
        var waiting = await RowAsync(first, timeout.Token);
        Assert.Null(waiting.PublishedAt);
        Assert.Equal(1, waiting.Attempts);
        Assert.Contains(ShopTopology.EmailQueue, waiting.LastError);
        var update = await GetAsync(live, liveQueue, timeout.Token);
        Assert.Equal(waiting.Id.ToString(), update.BasicProperties.MessageId);

        // Declare the correct queue through the same production connection setup used by a restarted host.
        await using (var repair = new RabbitMqConnection(new RabbitMqOptions
        {
            Host = _broker.Hostname, Port = _broker.GetMappedPublicPort(5672), UserName = "shop", Password = "shop",
        }, "outbox-repair-test", NullLogger<RabbitMqConnection>.Instance))
        {
            await repair.GetAsync(timeout.Token);
        }
        Assert.Equal(new OutboxPass(1, 1), await PassAsync(timeout.Token));
        Assert.NotNull((await RowAsync(first, timeout.Token)).PublishedAt);
        var email = await GetAsync(live, ShopTopology.EmailQueue, timeout.Token);
        Assert.Equal(waiting.Id.ToString(), email.BasicProperties.MessageId);
        Assert.Equal(ShopTopology.RoutingKeys.OrderPlaced, email.BasicProperties.Type);

        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.RecoverySucceededAsync += (_, _) => { recovered.TrySetResult(); return Task.CompletedTask; };
        // Restart the broker application at the same endpoint. Docker reallocates random host ports when a
        // container restarts, which would otherwise test a changed address instead of connection recovery.
        Assert.Equal(0, (await _broker.ExecAsync(["rabbitmqctl", "stop_app"], timeout.Token)).ExitCode);
        var second = await PlaceAsync(timeout.Token);
        Assert.Equal(new OutboxPass(1, 0), await PassAsync(timeout.Token));
        Assert.Null((await RowAsync(second, timeout.Token)).PublishedAt);

        Assert.Equal(0, (await _broker.ExecAsync(["rabbitmqctl", "start_app"], timeout.Token)).ExitCode);
        await recovered.Task.WaitAsync(timeout.Token);
        Assert.Equal(new OutboxPass(1, 1), await PassAsync(timeout.Token));
        var published = await RowAsync(second, timeout.Token);
        Assert.NotNull(published.PublishedAt);
        Assert.True(published.Attempts >= 2);
        Assert.Equal(published.Id.ToString(),
            (await GetAsync(live, ShopTopology.EmailQueue, timeout.Token)).BasicProperties.MessageId);
        Assert.Equal(new OutboxPass(0, 0), await PassAsync(timeout.Token));
    }

    private async Task<Guid> PlaceAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var order = Order.Place(Guid.NewGuid(), "customer@shop.test", [(Guid.NewGuid(), "Book", 10m, 1)], DateTimeOffset.UtcNow);
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return order.Id;
    }

    private async Task<OutboxPass> PassAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().PublishPendingAsync(ct);
    }

    private async Task<OutboxMessage> RowAsync(Guid orderId, CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        Assert.True(await db.Orders.AnyAsync(order => order.Id == orderId, ct));
        return await db.OutboxMessages.FromSql($"SELECT * FROM orders.outbox_messages WHERE payload->>'orderId' = {orderId.ToString()}")
            .AsNoTracking().SingleAsync(ct);
    }

    private static async Task<BasicGetResult> GetAsync(IChannel channel, string queue, CancellationToken ct)
    {
        while (true)
        {
            if (await channel.BasicGetAsync(queue, autoAck: true, ct) is { } delivery) return delivery;
            await Task.Delay(100, ct);
        }
    }
}
