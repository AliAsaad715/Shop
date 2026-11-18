using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using Shop.Messaging.Publishing;
using Xunit;

namespace Shop.Messaging.IntegrationTests;

/// <summary>
/// A host declares the plan's topology when it connects, read back here through the management API as an operator
/// would see it, and the client puts it back by itself after the broker drops the connection.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class TopologyTests(RabbitMqFixture fixture)
{
    [Fact]
    public async Task ConnectingDeclaresTheExchangesQueuesAndBindings()
    {
        using var host = await fixture.StartConnectedHostAsync();

        var events = await fixture.GetJsonAsync($"exchanges/%2F/{ShopTopology.EventsExchange}");
        Assert.Equal("topic", events.GetProperty("type").GetString());
        Assert.True(events.GetProperty("durable").GetBoolean());

        var deadLetters = await fixture.GetJsonAsync($"exchanges/%2F/{ShopTopology.DeadLetterExchange}");
        Assert.Equal("direct", deadLetters.GetProperty("type").GetString());
        Assert.True(deadLetters.GetProperty("durable").GetBoolean());

        var email = await fixture.GetJsonAsync($"queues/%2F/{ShopTopology.EmailQueue}");
        Assert.Equal("quorum", email.GetProperty("type").GetString());
        Assert.True(email.GetProperty("durable").GetBoolean());
        var arguments = email.GetProperty("arguments");
        Assert.Equal(5, arguments.GetProperty("x-delivery-limit").GetInt32());
        Assert.Equal("shop.dlx", arguments.GetProperty("x-dead-letter-exchange").GetString());
        Assert.Equal("email.order-confirmation", arguments.GetProperty("x-dead-letter-routing-key").GetString());
        Assert.Equal("at-least-once", arguments.GetProperty("x-dead-letter-strategy").GetString());
        Assert.Equal("reject-publish", arguments.GetProperty("x-overflow").GetString());

        var dlq = await fixture.GetJsonAsync($"queues/%2F/{ShopTopology.EmailDeadLetterQueue}");
        Assert.Equal("quorum", dlq.GetProperty("type").GetString());

        Assert.Empty(await BindingKeysAsync(ShopTopology.EventsExchange, ShopTopology.EmailQueue));
        Assert.Equal(["email.order-confirmation"], await BindingKeysAsync(ShopTopology.DeadLetterExchange, ShopTopology.EmailDeadLetterQueue));
        Assert.Equal(HealthStatus.Healthy, await RabbitMqFixture.HealthAsync(host));
    }

    [Fact]
    public async Task SecondHostDeclaringTheSameTopologyChangesNothing()
    {
        using var first = await fixture.StartConnectedHostAsync();
        var publisher = first.Services.GetRequiredService<IEventPublisher>();
        await fixture.PurgeAsync();
        Assert.True((await publisher.PublishAsync([RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced)], default))[0].Confirmed);

        // The API and the worker both declare it on start, in either order
        using var second = await fixture.StartConnectedHostAsync();

        Assert.Equal(HealthStatus.Healthy, await RabbitMqFixture.HealthAsync(second));
        Assert.Equal(1u, await fixture.CountAsync(ShopTopology.EmailQueue));
    }

    [Fact]
    public async Task UpgradeRemovesTheLegacyBindingWithoutDroppingQueuedEmailsOrEnqueuingDuplicates()
    {
        using var first = await fixture.StartConnectedHostAsync();
        await fixture.PurgeAsync();
        var publisher = first.Services.GetRequiredService<IEventPublisher>();
        var before = RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced);

        // Simulate the previous release, including a real waiting message published through its legacy route.
        await fixture.Probe.QueueBindAsync(ShopTopology.EmailQueue, ShopTopology.EventsExchange, ShopTopology.RoutingKeys.OrderPlaced);
        var connection = await first.Services.GetRequiredService<RabbitMqConnection>().GetAsync(default);
        await using var legacy = await connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await legacy.BasicPublishAsync(ShopTopology.EventsExchange, ShopTopology.RoutingKeys.OrderPlaced, mandatory: true,
            new BasicProperties { MessageId = before.MessageId.ToString(), Type = before.RoutingKey, DeliveryMode = DeliveryModes.Persistent }, before.Body);
        Assert.Equal(1u, await fixture.CountAsync(ShopTopology.EmailQueue));
        using var upgraded = await fixture.StartConnectedHostAsync();
        Assert.Empty(await BindingKeysAsync(ShopTopology.EventsExchange, ShopTopology.EmailQueue));
        var after = RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced);
        Assert.True(Assert.Single(await publisher.PublishAsync([after], default)).Confirmed);

        Assert.Equal(2u, await fixture.CountAsync(ShopTopology.EmailQueue));
        foreach (var message in new[] { before, after })
            Assert.Equal(message.MessageId.ToString(),
                (await RabbitMqFixture.GetSoonAsync(fixture.Probe, ShopTopology.EmailQueue, autoAck: true)).BasicProperties.MessageId);
        await fixture.Probe.QueueUnbindAsync(ShopTopology.EmailQueue, ShopTopology.EventsExchange, ShopTopology.RoutingKeys.OrderPlaced);
        await upgraded.StopAsync();
        await first.StopAsync();
    }

    [Fact]
    public async Task ClientReconnectsAndDeclaresTheTopologyAgainAfterTheBrokerDropsIt()
    {
        var clientName = RabbitMqFixture.NewClientName();
        using var host = await fixture.StartConnectedHostAsync(clientName);
        var connection = host.Services.GetRequiredService<RabbitMqConnection>();

        // As if the broker lost its data: the queues are gone, then the connection drops
        await fixture.Probe.QueueDeleteAsync(ShopTopology.EmailQueue, ifUnused: false, ifEmpty: false);
        await fixture.Probe.QueueDeleteAsync(ShopTopology.EmailDeadLetterQueue, ifUnused: false, ifEmpty: false);
        await fixture.ForceCloseConnectionAsync(clientName);
        await RabbitMqFixture.WaitUntilAsync(() => !connection.IsOpen, "the connection to drop");
        Assert.Equal(HealthStatus.Degraded, await RabbitMqFixture.HealthAsync(host));

        // The client retries every 5 seconds by default. It reports open as soon as the new connection is, and then
        // declares the recorded exchanges, queues and bindings
        await RabbitMqFixture.WaitUntilAsync(() => connection.IsOpen, "the client to reconnect");
        await RabbitMqFixture.WaitUntilAsync(
            async () => await fixture.ExistsAsync(ShopTopology.EmailQueue) && await fixture.ExistsAsync(ShopTopology.EmailDeadLetterQueue)
                && (await BindingKeysAsync(ShopTopology.EventsExchange, ShopTopology.EmailQueue)).Length == 0
                && (await BindingKeysAsync(ShopTopology.DeadLetterExchange, ShopTopology.EmailDeadLetterQueue)).SequenceEqual(["email.order-confirmation"]),
            "the client to declare the queues and bindings again", seconds: 10);
        var email = await fixture.GetJsonAsync($"queues/%2F/{ShopTopology.EmailQueue}");
        Assert.Equal("quorum", email.GetProperty("type").GetString());
        Assert.Equal(5, email.GetProperty("arguments").GetProperty("x-delivery-limit").GetInt32());
        Assert.Equal(HealthStatus.Healthy, await RabbitMqFixture.HealthAsync(host));
    }

    private async Task<string[]> BindingKeysAsync(string exchange, string queue)
    {
        var bindings = await fixture.GetJsonAsync($"bindings/%2F/e/{exchange}/q/{queue}");
        return [.. bindings.EnumerateArray().Select(binding => binding.GetProperty("routing_key").GetString()!)];
    }
}
