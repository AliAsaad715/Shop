using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using MimeKit;
using Shop.Messaging;
using Shop.Messaging.Consuming;
using Shop.Messaging.Publishing;
using Shop.Modules.Notifications.Data;
using Shop.Modules.Notifications.Email;
using Shop.Modules.Notifications.OrderConfirmation;
using Shop.Modules.Orders.Contracts;
using Testcontainers.RabbitMq;
using Xunit;
using Xunit.Abstractions;

namespace Shop.Modules.Notifications.IntegrationTests;

[Collection(NotificationsCollection.Name)]
public sealed class OrderConfirmationDeliveryTests(NotificationsFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4.3.7-management-alpine")
        .WithUsername("shop").WithPassword("shop").WithPortBinding(15672, true).Build();
    private readonly List<IHost> _workers = [];
    private IHost _publisherHost = null!;
    private IConnection _connection = null!;
    private IChannel _probe = null!;
    private HttpClient _management = null!;

    public async Task InitializeAsync()
    {
        fixture.Sender.Reset();
        await using (var scope = fixture.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().ProcessedMessages.ExecuteDeleteAsync();
        await _broker.StartAsync();
        _management = new HttpClient
        {
            BaseAddress = new Uri($"http://{_broker.Hostname}:{_broker.GetMappedPublicPort(15672)}/api/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        _management.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("shop:shop")));
        _publisherHost = CreateHost(worker: false);
        await _publisherHost.StartAsync();
        _connection = await _publisherHost.Services.GetRequiredService<RabbitMqConnection>().GetAsync(default);
        _probe = await _connection.CreateChannelAsync();
    }

    public async Task DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var worker in _workers)
        {
            await worker.StopAsync(timeout.Token);
            worker.Dispose();
        }
        if (_probe is not null) await _probe.DisposeAsync();
        if (_publisherHost is not null)
        {
            await _publisherHost.StopAsync();
            _publisherHost.Dispose();
        }
        _management?.Dispose();
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

    [Fact]
    public Task FiveFailedDeliveriesMakeFifteenSmtpAttemptsAndDeadLetterTheMessage() => RunAsync(async ct =>
    {
        fixture.Sender.FailNext([.. Enumerable.Range(0, 15).Select(_ => new IOException("Temporary SMTP failure"))]);
        var worker = await StartWorkerAsync(ct);
        var (id, order) = Assert.Single(await PublishAsync(1, ct));

        var dead = await DeadLetterAsync(ct);
        Assert.Equal(15, fixture.Sender.Attempts);
        Assert.Empty(fixture.Sender.Sent);
        Assert.Equal(0, await ProcessedCountAsync(id, ct));
        AssertDeadLetter(dead, id, order, "delivery_limit");
        await _probe.BasicAckAsync(dead.DeliveryTag, multiple: false, ct);
        await worker.StopAsync(ct);
        await AssertEmailQueueEmptyAsync(ct);
        Assert.Equal(15, fixture.Sender.Attempts); // A sixth delivery would make an extra attempt and succeed.
    });

    [Fact]
    public Task APermanentRecipientRefusalDeadLettersAfterOneSmtpAttempt() => RunAsync(async ct =>
    {
        fixture.Sender.FailNext(new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable,
            MailboxAddress.Parse("ada@shop.test"), "550 No such user"));
        var worker = await StartWorkerAsync(ct);
        var (id, order) = Assert.Single(await PublishAsync(1, ct));

        var dead = await DeadLetterAsync(ct);
        AssertDeadLetter(dead, id, order, "rejected");
        await _probe.BasicAckAsync(dead.DeliveryTag, multiple: false, ct);
        await worker.StopAsync(ct);
        Assert.Equal(1, fixture.Sender.Attempts);
        Assert.Empty(fixture.Sender.Sent);
        Assert.Equal(0, await ProcessedCountAsync(id, ct));
        await AssertEmailQueueEmptyAsync(ct);
    });

    [Fact]
    public Task AConnectionLostAfterRecordingButBeforeAckRedeliversWithoutSendingAgain() => RunAsync(async ct =>
    {
        var firstGate = new ConfirmationGate(ct, hold: true);
        var firstName = $"confirmation-crash-{Guid.NewGuid():N}";
        var first = await StartWorkerAsync(ct, firstGate, firstName);
        try
        {
            var (id, _) = Assert.Single(await PublishAsync(1, ct));
            await firstGate.Reached.Task.WaitAsync(ct);
            Assert.Equal(1, await ProcessedCountAsync(id, ct));
            Assert.Equal(1, fixture.Sender.Attempts);
            await WaitAsync(async () =>
            {
                var queue = await QueueAsync(ShopTopology.EmailQueue, ct);
                return queue.TryGetProperty("messages_unacknowledged", out var unacknowledged) && unacknowledged.GetInt32() == 1;
            }, ct);

            var connection = await first.Services.GetRequiredService<RabbitMqConnection>().GetAsync(ct);
            await CloseConnectionAsync(firstName, ct);
            // Shutdown notifications can be queued behind the held consumer callback. The close reason is set
            // before dispatching them, so observe the broker's close directly rather than waiting for that event.
            await WaitAsync(() => Task.FromResult(!connection.IsOpen), ct);
            Assert.False(connection.IsOpen);
            Assert.Equal(ShutdownInitiator.Peer, connection.CloseReason!.Initiator);
            // DisposeAsync cancels recovery even if its loop has not started yet. Disposal itself can wait for
            // consumer callbacks, so begin it before releasing the gate and await it afterwards.
            var disposingConnection = connection.DisposeAsync().AsTask();
            firstGate.Release();
            await disposingConnection.WaitAsync(ct);
            await first.StopAsync(ct);
            first.Dispose();
            _workers.Remove(first);
            Assert.Single(firstGate.Received);

            await WaitAsync(async () =>
                (await _probe.QueueDeclarePassiveAsync(ShopTopology.EmailQueue, ct)).MessageCount == 1, ct);
            var returned = Assert.IsType<BasicGetResult>(await _probe.BasicGetAsync(ShopTopology.EmailQueue, autoAck: false, ct));
            Assert.True(returned.Redelivered);
            Assert.Equal(id.ToString(), returned.BasicProperties.MessageId);
            Assert.Equal(1L, returned.BasicProperties.Headers!["x-delivery-count"]);
            await _probe.BasicNackAsync(returned.DeliveryTag, multiple: false, requeue: true, ct);

            var secondGate = new ConfirmationGate(ct, hold: false);
            var second = await StartWorkerAsync(ct, secondGate);
            await secondGate.Reached.Task.WaitAsync(ct);
            await second.StopAsync(ct); // Drain waits for the real consumer's ACK.
            var redelivered = Assert.Single(secondGate.Received);
            Assert.Equal(id, redelivered.MessageId);
            Assert.Equal(1, redelivered.FailedDeliveries); // The probe's nack did not spend another failure.
            Assert.Equal(1, fixture.Sender.Attempts);
            Assert.Equal(id, Assert.Single(fixture.Sender.Sent).Id);
            Assert.Equal(1, await ProcessedCountAsync(id, ct));
            await AssertEmailQueueEmptyAsync(ct);
            Assert.Equal(0u, (await _probe.QueueDeclarePassiveAsync(ShopTopology.EmailDeadLetterQueue, ct)).MessageCount);
        }
        finally
        {
            firstGate.Release(); // Cancellation of Host.StopAsync alone does not release a handler's token.
        }
    });

    private IHost CreateHost(bool worker, ConfirmationGate? gate = null, string? clientName = null)
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
            ["Email:From"] = "Shop <orders@shop.local>",
            ["Email:Provider"] = "Mailpit",
            ["Email:SendAttempts"] = "3",
            ["Email:RetryDelay"] = "00:00:00.010",
            ["Email:Mailpit:Host"] = "localhost",
            ["Email:Mailpit:Port"] = "1025",
        });
        builder.Services.AddLogging();
        builder.Services.AddShopMessaging(clientName ?? $"confirmation-test-{Guid.NewGuid():N}");
        if (worker)
        {
            // Exercise the production worker registration and inbox; replace only the external SMTP boundary.
            builder.Services.AddSingleton<IEmailSender>(fixture.Sender);
            var existingHostedServices = builder.Services.Where(service => service.ServiceType == typeof(IHostedService)).ToArray();
            builder.Services.AddNotificationsWorker();
            if (gate is not null)
            {
                // AddQueueConsumer registers a factory, not an ImplementationType. Remove only the descriptor
                // added by AddNotificationsWorker; retain the connector and the real scoped handler.
                var consumer = Assert.Single(builder.Services, service =>
                    service.ServiceType == typeof(IHostedService) && !existingHostedServices.Contains(service));
                Assert.NotNull(consumer.ImplementationFactory);
                builder.Services.Remove(consumer);
                builder.Services.AddSingleton(gate);
                builder.Services.AddQueueConsumer<GatedConfirmationHandler>(ShopTopology.EmailQueue, prefetch: 5);
            }
        }
        return builder.Build();
    }

    private async Task<IHost> StartWorkerAsync(CancellationToken ct, ConfirmationGate? gate = null, string? clientName = null)
    {
        var worker = CreateHost(worker: true, gate, clientName);
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

    private async Task<int> ProcessedCountAsync(Guid id, CancellationToken ct)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().ProcessedMessages
            .CountAsync(message => message.MessageId == id, ct);
    }

    private async Task<JsonElement> QueueAsync(string queue, CancellationToken ct)
    {
        using var response = await _management.GetAsync($"queues/%2F/{Uri.EscapeDataString(queue)}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private Task AssertEmailQueueEmptyAsync(CancellationToken ct) => WaitAsync(async () =>
    {
        var queue = await QueueAsync(ShopTopology.EmailQueue, ct);
        // Management statistics appear after the first sampling interval on a newly created queue.
        return queue.TryGetProperty("messages_ready", out var ready) && ready.GetInt32() == 0
            && queue.TryGetProperty("messages_unacknowledged", out var unacknowledged) && unacknowledged.GetInt32() == 0;
    }, ct);

    private async Task<BasicGetResult> DeadLetterAsync(CancellationToken ct)
    {
        await WaitAsync(async () =>
            (await _probe.QueueDeclarePassiveAsync(ShopTopology.EmailDeadLetterQueue, ct)).MessageCount == 1, ct);
        return Assert.IsType<BasicGetResult>(await _probe.BasicGetAsync(ShopTopology.EmailDeadLetterQueue, autoAck: false, ct));
    }

    private static void AssertDeadLetter(BasicGetResult dead, Guid id, OrderPlacedEvent order, string reason)
    {
        Assert.Equal(id.ToString(), dead.BasicProperties.MessageId);
        Assert.Equal(ShopTopology.RoutingKeys.OrderPlaced, dead.BasicProperties.Type);
        Assert.Equal(reason, Encoding.UTF8.GetString(Assert.IsType<byte[]>(dead.BasicProperties.Headers!["x-first-death-reason"])));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(order, new JsonSerializerOptions(JsonSerializerDefaults.Web)), dead.Body.ToArray());
    }

    private async Task CloseConnectionAsync(string clientName, CancellationToken ct)
    {
        string? connectionName = null;
        await WaitAsync(async () =>
        {
            var connections = await _management.GetFromJsonAsync<JsonElement>("connections", ct);
            connectionName = connections.EnumerateArray()
                .Where(connection => connection.GetProperty("client_properties").TryGetProperty("connection_name", out var name)
                    && name.GetString() == clientName)
                .Select(connection => connection.GetProperty("name").GetString()).SingleOrDefault();
            return connectionName is not null;
        }, ct);
        using var response = await _management.DeleteAsync($"connections/{Uri.EscapeDataString(connectionName!)}", ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task RunAsync(Func<CancellationToken, Task> scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await scenario(timeout.Token);
        }
        catch
        {
            using var diagnostics = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                output.WriteLine($"Email attempts: {fixture.Sender.Attempts}; successful: {fixture.Sender.Sent.Count}");
                foreach (var path in new[] { $"queues/%2F/{ShopTopology.EmailQueue}", $"queues/%2F/{ShopTopology.EmailDeadLetterQueue}", "connections" })
                    output.WriteLine($"{path}: {await _management.GetStringAsync(path, diagnostics.Token)}");
                var (stdout, stderr) = await _broker.GetLogsAsync(ct: diagnostics.Token);
                output.WriteLine(string.Join(Environment.NewLine, stdout.Split('\n').TakeLast(60)));
                output.WriteLine(string.Join(Environment.NewLine, stderr.Split('\n').TakeLast(20)));
            }
            catch (Exception error)
            {
                output.WriteLine($"Diagnostics unavailable: {error.Message}");
            }
            throw;
        }
    }

    internal sealed class ConfirmationGate(CancellationToken testToken, bool hold)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<ReceivedMessage> Received { get; } = new();
        public void Release() => _released.TrySetResult();

        public async Task WaitAsync(CancellationToken handlerToken)
        {
            Reached.TrySetResult();
            if (!hold) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(testToken, handlerToken);
            await _released.Task.WaitAsync(linked.Token);
        }
    }

    internal sealed class GatedConfirmationHandler(OrderConfirmationHandler inner, ConfirmationGate gate) : IMessageHandler
    {
        public async Task<MessageOutcome> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
        {
            gate.Received.Enqueue(message);
            var outcome = await inner.HandleAsync(message, cancellationToken);
            if (outcome == MessageOutcome.Done) await gate.WaitAsync(cancellationToken);
            return outcome;
        }
    }
}
