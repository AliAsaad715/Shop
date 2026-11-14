using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shop.Messaging.Publishing;
using Xunit;

namespace Shop.Messaging.IntegrationTests;

/// <summary>
/// RabbitMQ being down must never stop a host or throw at a caller, while a wrong <c>RabbitMq</c> section must stop
/// the host at once. Nothing listens on port 1, so these need no container.
/// </summary>
public sealed class UnreachableBrokerTests
{
    private static readonly Dictionary<string, string?> NoBroker = new()
    {
        ["RabbitMq:Host"] = "127.0.0.1",
        ["RabbitMq:Port"] = "1",
        ["RabbitMq:UserName"] = "shop",
        ["RabbitMq:Password"] = "shop",
    };

    [Fact]
    public async Task HostStartsAtOnceAndReportsDegraded()
    {
        using var host = RabbitMqFixture.BuildHost(new(NoBroker));

        var started = Stopwatch.StartNew();
        await host.StartAsync();
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"Startup waited {started.Elapsed} for RabbitMQ.");
        Assert.Equal(HealthStatus.Degraded, await RabbitMqFixture.HealthAsync(host));
        await host.StopAsync();
    }

    [Fact]
    public async Task EveryMessageFailsInsteadOfThrowing()
    {
        using var host = RabbitMqFixture.BuildHost(new(NoBroker));
        var publisher = host.Services.GetRequiredService<IEventPublisher>();
        var batch = new[] { RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced), RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced) };

        var results = await publisher.PublishAsync(batch, default);

        Assert.Equal(batch.Select(message => message.MessageId), results.Select(result => result.MessageId));
        Assert.All(results, result =>
        {
            Assert.False(result.Confirmed);
            Assert.StartsWith("RabbitMQ can't be reached", result.Failure);
        });
    }

    [Fact]
    public async Task CancellingThrowsRatherThanReportingFailures()
    {
        using var host = RabbitMqFixture.BuildHost(new(NoBroker));
        var publisher = host.Services.GetRequiredService<IEventPublisher>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            publisher.PublishAsync([RabbitMqFixture.Event(ShopTopology.RoutingKeys.OrderPlaced)], cancelled.Token));
    }

    [Theory]
    [InlineData("RabbitMq:Host", "")]
    [InlineData("RabbitMq:Port", "0")]
    [InlineData("RabbitMq:UserName", "")]
    [InlineData("RabbitMq:Password", "")]
    [InlineData("RabbitMq:ConfirmTimeout", "00:00:00")]
    [InlineData("RabbitMq:ConnectTimeout", "00:05:00")]
    public async Task InvalidSettingsStopTheHostAtStartup(string key, string value)
    {
        using var host = RabbitMqFixture.BuildHost(new(NoBroker), new() { [key] = value });

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }
}
