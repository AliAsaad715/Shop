using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shop.Messaging;
using Shop.Messaging.Publishing;
using Shop.Modules.Notifications.Data;
using Shop.Modules.Notifications.Email;
using Shop.Modules.Orders.Contracts;
using Testcontainers.RabbitMq;
using Xunit;

namespace Shop.Modules.Notifications.IntegrationTests;

[Collection(NotificationsCollection.Name)]
public sealed class OrderConfirmationDeliveryTests(NotificationsFixture fixture) : IAsyncLifetime
{
    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4.3.7-management-alpine")
        .WithUsername("shop").WithPassword("shop").Build();
    private readonly List<IHost> _workers = [];
    private IHost _publisherHost = null!;
    private IConnection _connection = null!;
    private IChannel _probe = null!;

    public async Task InitializeAsync()
    {
        fixture.Sender.Reset();
        await using (var scope = fixture.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().ProcessedMessages.ExecuteDeleteAsync();
        await _broker.StartAsync();
        _publisherHost = CreateHost(worker: false);
        await _publisherHost.StartAsync();
        _connection = await _publisherHost.Services.GetRequiredService<RabbitMqConnection>().GetAsync(default);
        _probe = await _connection.CreateChannelAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var worker in _workers)
        {
            await worker.StopAsync();
            worker.Dispose();
        }
        if (_probe is not null) await _probe.DisposeAsync();
        if (_publisherHost is not null)
        {
            await _publisherHost.StopAsync();
            _publisherHost.Dispose();
        }
        await _broker.DisposeAsync();
    }

    [Fact]
    public async Task StoppingTheWorkerAndRestartingTheBrokerKeepsEveryConfirmationEmail()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var worker = await StartWorkerAsync(timeout.Token);
        await worker.StopAsync(timeout.Token);
        await WaitAsync(async () => (await _probe.QueueDeclarePassiveAsync(ShopTopology.EmailQueue, timeout.Token)).ConsumerCount == 0, timeout.Token);

        var first = await PublishAsync(3, timeout.Token);
        Assert.Empty(fixture.Sender.Sent);
        Assert.Equal(3u, (await _probe.QueueDeclarePassiveAsync(ShopTopology.EmailQueue, timeout.Token)).MessageCount);

        worker = await StartWorkerAsync(timeout.Token);
        await WaitAsync(() => Task.FromResult(fixture.Sender.Sent.Count == 3), timeout.Token);
        await worker.StopAsync(timeout.Token); // Drain includes recording the message and acknowledging it.
        Assert.Equal(0u, (await _probe.QueueDeclarePassiveAsync(ShopTopology.EmailQueue, timeout.Token)).MessageCount);

        var second = await PublishAsync(2, timeout.Token);
        Assert.Equal(3, fixture.Sender.Sent.Count);
        Assert.Equal(2u, (await _probe.QueueDeclarePassiveAsync(ShopTopology.EmailQueue, timeout.Token)).MessageCount);

        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.RecoverySucceededAsync += (_, _) => { recovered.TrySetResult(); return Task.CompletedTask; };
        // Keep Docker's mapped endpoint stable while stopping and restarting the actual RabbitMQ application.
        Assert.Equal(0, (await _broker.ExecAsync(["rabbitmqctl", "stop_app"], timeout.Token)).ExitCode);
        Assert.Equal(0, (await _broker.ExecAsync(["rabbitmqctl", "start_app"], timeout.Token)).ExitCode);
        await recovered.Task.WaitAsync(timeout.Token);
        Assert.Equal(2u, (await _probe.QueueDeclarePassiveAsync(ShopTopology.EmailQueue, timeout.Token)).MessageCount);

        worker = await StartWorkerAsync(timeout.Token);
        await WaitAsync(() => Task.FromResult(fixture.Sender.Sent.Count == 5), timeout.Token);
        await worker.StopAsync(timeout.Token);
        Assert.Equal(0u, (await _probe.QueueDeclarePassiveAsync(ShopTopology.EmailQueue, timeout.Token)).MessageCount);

        var expected = first.Concat(second).ToArray();
        Assert.Equal(expected.Length, fixture.Sender.Sent.Count);
        foreach (var (_, order) in expected)
            Assert.Single(fixture.Sender.Sent, email => email.To == order.CustomerEmail && email.TextBody.Contains(order.OrderId.ToString()));

        await using var scope = fixture.Services.CreateAsyncScope();
        var recorded = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().ProcessedMessages.AsNoTracking().ToListAsync(timeout.Token);
        Assert.Equal(expected.Select(message => message.Id).Order(), recorded.Select(message => message.MessageId).Order());
        Assert.All(recorded, message => Assert.Equal(ShopTopology.RoutingKeys.OrderPlaced, message.Type));
    }

    private IHost CreateHost(bool worker)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = fixture.Services.GetRequiredService<IConfiguration>().GetConnectionString("Shop"),
            ["ReadRouting:Enabled"] = "false",
            ["RabbitMq:Host"] = _broker.Hostname,
            ["RabbitMq:Port"] = _broker.GetMappedPublicPort(5672).ToString(),
            ["RabbitMq:UserName"] = "shop",
            ["RabbitMq:Password"] = "shop",
        });
        builder.Services.AddLogging();
        builder.Services.AddShopMessaging($"confirmation-test-{Guid.NewGuid():N}");
        if (worker)
        {
            // Exercise the production worker registration and inbox; replace only the external SMTP boundary.
            builder.Services.AddSingleton<IEmailSender>(fixture.Sender);
            builder.Services.AddNotificationsWorker();
        }
        return builder.Build();
    }

    private async Task<IHost> StartWorkerAsync(CancellationToken ct)
    {
        var worker = CreateHost(worker: true);
        _workers.Add(worker);
        await worker.StartAsync(ct);
        await WaitAsync(async () => (await _probe.QueueDeclarePassiveAsync(ShopTopology.EmailQueue, ct)).ConsumerCount == 1, ct);
        return worker;
    }

    private async Task<(Guid Id, OrderPlacedEvent Order)[]> PublishAsync(int count, CancellationToken ct)
    {
        var orders = Enumerable.Range(0, count).Select(_ => (
            Id: Guid.NewGuid(), Order: NotificationsFixture.Order($"{Guid.NewGuid():N}@shop.test"))).ToArray();
        var results = await _publisherHost.Services.GetRequiredService<IEventPublisher>().PublishAsync(
            [.. orders.Select(item => new OutgoingMessage(item.Id, ShopTopology.RoutingKeys.OrderPlaced,
                JsonSerializer.SerializeToUtf8Bytes(item.Order, new JsonSerializerOptions(JsonSerializerDefaults.Web)), item.Order.PlacedAt))], ct);
        Assert.All(results, result => Assert.True(result.Confirmed, result.Failure));
        return orders;
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        while (!await condition()) await Task.Delay(100, ct);
    }
}
