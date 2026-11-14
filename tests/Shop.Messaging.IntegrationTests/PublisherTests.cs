using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shop.Messaging.Publishing;
using Xunit;

namespace Shop.Messaging.IntegrationTests;

/// <summary>
/// A publish counts only once RabbitMQ confirms it: confirmed messages are stored and persistent, an unroutable one
/// fails instead of vanishing, and failed emails reach the dead-letter queue.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class PublisherTests(RabbitMqFixture fixture) : IAsyncLifetime
{
    private IHost _host = null!;
    private IEventPublisher _publisher = null!;

    public async Task InitializeAsync()
    {
        _host = await fixture.StartConnectedHostAsync();
        _publisher = _host.Services.GetRequiredService<IEventPublisher>();
        await fixture.PurgeAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task ConfirmedOrderPlacedEventWaitsInTheEmailQueueAsAPersistentMessage()
    {
        var message = RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced, """{"orderNumber":"SO-1001"}""");

        var results = await _publisher.PublishAsync([message], default);

        Assert.Equal(PublishResult.Success(message.MessageId), Assert.Single(results));
        var stored = await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailQueue, autoAck: true);
        Assert.Equal(message.MessageId.ToString(), stored.BasicProperties.MessageId);
        Assert.Equal("order.placed", stored.BasicProperties.Type);
        Assert.Equal("application/json", stored.BasicProperties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, stored.BasicProperties.DeliveryMode);
        Assert.Equal(message.OccurredAt.ToUnixTimeSeconds(), stored.BasicProperties.Timestamp.UnixTime);
        Assert.Equal("""{"orderNumber":"SO-1001"}""", Encoding.UTF8.GetString(stored.Body.Span));
    }

    [Fact]
    public async Task BatchIsConfirmedAndQueuedInListOrder()
    {
        var batch = Enumerable.Range(1, 100)
            .Select(n => RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced, $$"""{"n":{{n}}}"""))
            .ToList();

        var results = await _publisher.PublishAsync(batch, default);

        Assert.All(results, result => Assert.True(result.Confirmed, result.Failure));
        Assert.Equal(batch.Select(message => message.MessageId), results.Select(result => result.MessageId));
        Assert.Equal(100u, await fixture.CountAsync(ShopTopology.EmailQueue));
        var queued = new List<string>();
        for (var i = 0; i < batch.Count; i++)
        {
            queued.Add((await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailQueue, autoAck: true)).BasicProperties.MessageId!);
        }

        Assert.Equal(batch.Select(message => message.MessageId.ToString()), queued);
    }

    [Fact]
    public async Task MessageNoQueueIsBoundForFailsWhileTheRestOfTheBatchIsConfirmed()
    {
        // No queue takes status changes until the live-update queues arrive (R4-07)
        var unroutable = RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderStatusChanged);
        var routable = RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced);

        var results = await _publisher.PublishAsync([unroutable, routable], default);

        Assert.False(results[0].Confirmed);
        Assert.Contains("No queue is bound for 'order.status-changed'", results[0].Failure);
        Assert.True(results[1].Confirmed, results[1].Failure);
        Assert.Equal(1u, await fixture.CountAsync(ShopTopology.EmailQueue));

        // The channel survives a return, so the next batch goes through
        Assert.True((await _publisher.PublishAsync([RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced)], default))[0].Confirmed);
    }

    [Fact]
    public async Task RejectedEmailMessageMovesToTheDeadLetterQueue()
    {
        var message = RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced);
        Assert.True((await _publisher.PublishAsync([message], default))[0].Confirmed);

        // What the worker will do with an email that can never be sent (R4-05)
        var delivery = await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailQueue, autoAck: false);
        await fixture.Probe.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false);

        var dead = await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailDeadLetterQueue, autoAck: true);
        Assert.Equal(message.MessageId.ToString(), dead.BasicProperties.MessageId);
        Assert.Equal("rejected", RabbitMqFixture.Header(dead.BasicProperties, "x-first-death-reason"));
        Assert.Equal(ShopTopology.EmailQueue, RabbitMqFixture.Header(dead.BasicProperties, "x-first-death-queue"));
        Assert.Equal(0u, await fixture.CountAsync(ShopTopology.EmailQueue));
    }

    [Fact]
    public async Task EmailQueueDeadLettersAMessageOnceItsDeliveryLimitIsUsedUp()
    {
        var message = RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced);
        Assert.True((await _publisher.PublishAsync([message], default))[0].Confirmed);

        // Every delivery fails and is rejected with requeue, as the worker will while the mail server is down (R4-05).
        // A requeued message takes a moment to be ready again, so this counts until the message shows up in the DLQ
        List<string> failedSoFar = [];
        await RabbitMqFixture.WaitUntilAsync(async () =>
        {
            if (await fixture.Probe.BasicGetAsync(ShopTopology.EmailQueue, autoAck: false) is { } delivery)
            {
                failedSoFar.Add(DeliveryCount(delivery));
                Assert.True(failedSoFar.Count <= 20, "The delivery limit never took effect.");
                await fixture.Probe.BasicRejectAsync(delivery.DeliveryTag, requeue: true);
            }

            return await fixture.CountAsync(ShopTopology.EmailDeadLetterQueue) == 1;
        }, "the message to reach the DLQ");

        var dead = await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailDeadLetterQueue, autoAck: true);
        Assert.Equal(message.MessageId.ToString(), dead.BasicProperties.MessageId);
        Assert.Equal("delivery_limit", RabbitMqFixture.Header(dead.BasicProperties, "x-first-death-reason"));
        Assert.Equal(0u, await fixture.CountAsync(ShopTopology.EmailQueue));

        // x-delivery-count says how many deliveries failed before this one. With a limit of 5 the sixth failure
        // dead-letters the message: the first try plus five retries
        Assert.Equal(["0", "1", "2", "3", "4", "5"], failedSoFar);
    }

    [Fact]
    public async Task NackWithRequeueNeverCountsTowardsTheDeliveryLimit()
    {
        var message = RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced);
        Assert.True((await _publisher.PublishAsync([message], default))[0].Confirmed);

        // RabbitMQ 4.3+ treats basic.nack as "give it back", not as a failure, so a worker that nacks a broken
        // email would retry it forever. Twice the limit here, and the message is still in the email queue
        for (var i = 0; i < ShopTopology.EmailDeliveryLimit * 2; i++)
        {
            var delivery = await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailQueue, autoAck: false);
            Assert.Equal("0", DeliveryCount(delivery));
            await fixture.Probe.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
        }

        var still = await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailQueue, autoAck: true);
        Assert.Equal(message.MessageId.ToString(), still.BasicProperties.MessageId);
        Assert.Equal(0u, await fixture.CountAsync(ShopTopology.EmailDeadLetterQueue));
    }

    // Absent on a first delivery
    private static string DeliveryCount(BasicGetResult delivery) =>
        delivery.BasicProperties.Headers?.ContainsKey("x-delivery-count") == true
            ? RabbitMqFixture.Header(delivery.BasicProperties, "x-delivery-count")
            : "0";
}
