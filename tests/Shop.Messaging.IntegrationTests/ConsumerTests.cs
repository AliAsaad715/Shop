using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Shop.Messaging.Consuming;
using Shop.Messaging.Publishing;
using Xunit;

namespace Shop.Messaging.IntegrationTests;

/// <summary>
/// The queue consumer the email worker runs (task R4-03): it acknowledges only what its handler finished, turns a
/// failure into a counted redelivery, keeps at most the prefetch in flight, comes back after the broker drops it, and
/// stops without losing a message or spending its retries.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class ConsumerTests(RabbitMqFixture fixture) : IAsyncLifetime
{
    private const ushort Prefetch = 2;
    private readonly HandlerScript _script = new();
    private readonly LogCapture _logs = new();
    private readonly string _clientName = RabbitMqFixture.NewClientName();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        // Clean queues first: the consumer would take anything left over the moment it starts
        using (var setup = await fixture.StartConnectedHostAsync())
        {
            await fixture.PurgeAsync();
            await setup.StopAsync();
        }

        _host = await fixture.StartConnectedHostAsync(_clientName, services => services
            .AddSingleton(_script)
            .AddSingleton<ILoggerProvider>(_logs)
            .AddQueueConsumer<ScriptedHandler>(ShopTopology.EmailQueue, Prefetch));
        await WaitForConsumersAsync(1);
    }

    public async Task DisposeAsync()
    {
        _script.Release();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task AMessageIsAcknowledgedOnlyOnceItsHandlerIsDone()
    {
        var message = await PublishAsync();

        await RabbitMqFixture.WaitUntilAsync(() => _script.Received.Count == 1, "the handler to get it");
        await _host.StopAsync();

        // Acknowledged: stopping returns nothing to the queue
        Assert.Equal(message.MessageId, Assert.Single(_script.Received).MessageId);
        Assert.Equal(0u, await fixture.CountAsync(ShopTopology.EmailQueue));
    }

    [Fact]
    public async Task RetryRejectsSoTheDeliveryLimitMovesTheMessageToTheDlq()
    {
        _script.Outcome = _ => MessageOutcome.Retry;
        var message = await PublishAsync();

        await RabbitMqFixture.WaitUntilAsync(async () => await fixture.CountAsync(ShopTopology.EmailDeadLetterQueue) == 1, "the DLQ");

        // Each try counted, as basic.reject does: the first try plus five retries
        Assert.Equal([0, 1, 2, 3, 4, 5], _script.Received.Select(received => received.FailedDeliveries));
        var dead = await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailDeadLetterQueue, autoAck: true);
        Assert.Equal(message.MessageId.ToString(), dead.BasicProperties.MessageId);
        Assert.Equal("delivery_limit", RabbitMqFixture.Header(dead.BasicProperties, "x-first-death-reason"));
    }

    [Fact]
    public async Task RejectSendsTheMessageStraightToTheDlq()
    {
        _script.Outcome = _ => MessageOutcome.Reject;
        await PublishAsync();

        var dead = await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailDeadLetterQueue, autoAck: true);

        Assert.Single(_script.Received);
        Assert.Equal("rejected", RabbitMqFixture.Header(dead.BasicProperties, "x-first-death-reason"));
    }

    [Fact]
    public async Task AHandlerThatThrowsGetsTheMessageAgain()
    {
        _script.Outcome = received => received.FailedDeliveries == 0 ? throw new InvalidOperationException("Mail server down") : MessageOutcome.Done;
        await PublishAsync();

        await RabbitMqFixture.WaitUntilAsync(() => _script.Received.Count == 2, "the second delivery");
        await _host.StopAsync();

        Assert.Equal([0, 1], _script.Received.Select(received => received.FailedDeliveries));
        Assert.Equal(0u, await fixture.CountAsync(ShopTopology.EmailQueue));
    }

    [Fact]
    public async Task NoMoreThanThePrefetchIsInFlight()
    {
        _script.Hold();
        for (var i = 0; i < 5; i++)
        {
            await PublishAsync();
        }

        // One in the handler and one waiting behind it; the other three stay ready in the queue for another worker
        await RabbitMqFixture.WaitUntilAsync(async () => await fixture.CountAsync(ShopTopology.EmailQueue) == 5u - Prefetch, "the prefetch");
        Assert.Single(_script.Received);

        _script.Release();
        await RabbitMqFixture.WaitUntilAsync(() => _script.Received.Count == 5, "all five");
    }

    [Fact]
    public async Task StoppingFinishesTheCurrentMessageAndReturnsThePrefetchedOneWithoutCountingIt()
    {
        _script.Hold();
        var current = await PublishAsync();
        var prefetched = await PublishAsync();
        var waiting = await PublishAsync();
        await RabbitMqFixture.WaitUntilAsync(() => _script.Received.Count == 1, "the first message to start");
        await RabbitMqFixture.WaitUntilAsync(async () => await fixture.CountAsync(ShopTopology.EmailQueue) == 1, "the prefetch");

        // docker compose stop: no new deliveries, then the message in the handler finishes. RabbitMQ keeps counting a
        // cancelled consumer until its messages are answered, so the consumer's own log says when it has cancelled
        var stopping = _host.StopAsync();
        await RabbitMqFixture.WaitUntilAsync(() => _logs.Messages.Any(line => line.StartsWith("Stopped taking messages")), "the consumer to cancel");
        _script.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(current.MessageId, Assert.Single(_script.Received).MessageId);
        var left = new List<BasicGetResult>
        {
            await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailQueue, autoAck: true),
            await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailQueue, autoAck: true),
        };
        Assert.Equal(0u, await fixture.CountAsync(ShopTopology.EmailQueue));

        // The prefetched one was delivered and handed back with a nack, which RabbitMQ doesn't count as a failure
        var returned = left.Single(message => message.BasicProperties.MessageId == prefetched.MessageId.ToString());
        Assert.True(returned.Redelivered);
        Assert.Equal(0, FailedDeliveries(returned));
        var untouched = left.Single(message => message.BasicProperties.MessageId == waiting.MessageId.ToString());
        Assert.False(untouched.Redelivered);
    }

    [Fact]
    public async Task TheConsumerComesBackAfterTheBrokerDropsTheConnection()
    {
        var connection = _host.Services.GetRequiredService<RabbitMqConnection>();
        await fixture.ForceCloseConnectionAsync(_clientName);
        await RabbitMqFixture.WaitUntilAsync(() => !connection.IsOpen, "the connection to drop");
        await RabbitMqFixture.WaitUntilAsync(() => connection.IsOpen, "the client to reconnect");

        var message = await PublishAsync();

        await RabbitMqFixture.WaitUntilAsync(() => _script.Received.Any(received => received.MessageId == message.MessageId), "the consumer to get it");
    }

    [Fact]
    public async Task AQueueDeletedUnderTheConsumerIsDeclaredAgainAndConsumed()
    {
        await fixture.Probe.QueueDeleteAsync(ShopTopology.EmailQueue, ifUnused: false, ifEmpty: false);

        // RabbitMQ cancels the consumer; it starts again a second later, declaring the queue first
        await RabbitMqFixture.WaitUntilAsync(() => fixture.ExistsAsync(ShopTopology.EmailQueue).Result, "the queue to be declared again");
        await WaitForConsumersAsync(1);
        var message = await PublishAsync();

        await RabbitMqFixture.WaitUntilAsync(() => _script.Received.Any(received => received.MessageId == message.MessageId), "the consumer to get it");
    }

    private async Task<OutgoingMessage> PublishAsync()
    {
        var message = new OutgoingMessage(Guid.CreateVersion7(), ShopTopology.RoutingKeys.OrderPlaced, Encoding.UTF8.GetBytes("{}"), DateTimeOffset.UtcNow);
        var result = Assert.Single(await _host.Services.GetRequiredService<IEventPublisher>().PublishAsync([message], default));
        Assert.True(result.Confirmed, result.Failure);
        return message;
    }

    private Task WaitForConsumersAsync(uint consumers) => RabbitMqFixture.WaitUntilAsync(
        async () => (await fixture.Probe.QueueDeclarePassiveAsync(ShopTopology.EmailQueue)).ConsumerCount == consumers,
        $"{consumers} consumer(s) on the email queue");

    private static int FailedDeliveries(BasicGetResult message) =>
        message.BasicProperties.Headers?.ContainsKey("x-delivery-count") == true
            ? int.Parse(RabbitMqFixture.Header(message.BasicProperties, "x-delivery-count"))
            : 0;

    // Shared by the test and every handler instance: what came in, what to answer, and whether to hold a message
    internal sealed class HandlerScript
    {
        private TaskCompletionSource _gate = Open();

        public ConcurrentQueue<ReceivedMessage> Received { get; } = new();

        public Func<ReceivedMessage, MessageOutcome> Outcome { get; set; } = _ => MessageOutcome.Done;

        public Task Gate => _gate.Task;

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        private static TaskCompletionSource Open()
        {
            var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            open.SetResult();
            return open;
        }
    }

    // Keeps every log message, so a test can wait for something the consumer reports
    internal sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }

    internal sealed class ScriptedHandler(HandlerScript script) : IMessageHandler
    {
        public async Task<MessageOutcome> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
        {
            script.Received.Enqueue(message);
            await script.Gate.WaitAsync(cancellationToken);
            return script.Outcome(message);
        }
    }
}
