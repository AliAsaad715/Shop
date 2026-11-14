using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shop.Messaging.Publishing;
using Testcontainers.RabbitMq;
using Xunit;

namespace Shop.Messaging.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "RabbitMQ";
}

/// <summary>
/// The same RabbitMQ image as compose, with the management plugin, so tests can read queue arguments and close
/// connections the way an operator would. <see cref="Probe"/> is a separate connection for looking at queues.
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    public const string Image = "rabbitmq:4.3.7-management-alpine";
    private const string User = "shop";
    private const string Password = "shop";

    private readonly RabbitMqContainer _container = new RabbitMqBuilder(Image)
        .WithUsername(User)
        .WithPassword(Password)
        .WithPortBinding(15672, true)
        .Build();

    private IConnection _probe = null!;

    public HttpClient Management { get; private set; } = null!;

    /// <summary>A channel of the tests' own, never the host's; a broker error on it is repaired before the next test.</summary>
    public IChannel Probe { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _probe = await new ConnectionFactory { Uri = new Uri(_container.GetConnectionString()) }.CreateConnectionAsync();
        Probe = await _probe.CreateChannelAsync();
        Management = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(15672)}/api/") };
        Management.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")));
    }

    public async Task DisposeAsync()
    {
        Management?.Dispose();
        await Probe.DisposeAsync();
        await _probe.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// A host with only the messaging services, configured like the API but pointing at this container. Each host
    /// gets its own connection name, so a test can find and close exactly its connection.
    /// </summary>
    public IHost CreateHost(string? clientName = null, Dictionary<string, string?>? overrides = null) => BuildHost(new Dictionary<string, string?>
    {
        ["RabbitMq:Host"] = _container.Hostname,
        ["RabbitMq:Port"] = _container.GetMappedPublicPort(5672).ToString(),
        ["RabbitMq:UserName"] = User,
        ["RabbitMq:Password"] = Password,
    }, overrides, clientName);

    public static IHost BuildHost(
        Dictionary<string, string?> settings, Dictionary<string, string?>? overrides = null, string? clientName = null)
    {
        foreach (var setting in overrides ?? [])
        {
            settings[setting.Key] = setting.Value;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        builder.Services.AddShopMessaging(clientName ?? NewClientName());
        return builder.Build();
    }

    public static string NewClientName() => $"shop-tests-{Guid.NewGuid():N}"[..19];

    /// <summary>Starts the host and waits until its connector has connected and declared the topology.</summary>
    public async Task<IHost> StartConnectedHostAsync(string? clientName = null)
    {
        await EnsureProbeAsync();
        var host = CreateHost(clientName);
        await host.StartAsync();
        await WaitUntilAsync(() => host.Services.GetRequiredService<RabbitMqConnection>().IsOpen, "the host to connect");
        return host;
    }

    public async Task PurgeAsync()
    {
        await EnsureProbeAsync();
        foreach (var queue in new[] { ShopTopology.EmailQueue, ShopTopology.EmailDeadLetterQueue })
        {
            if (await ExistsAsync(queue))
            {
                await Probe.QueuePurgeAsync(queue);
            }
        }
    }

    /// <summary>Ready messages only; a message a channel holds unacknowledged isn't counted.</summary>
    public async Task<uint> CountAsync(string queue) => (await Probe.QueueDeclarePassiveAsync(queue)).MessageCount;

    public async Task<bool> ExistsAsync(string queue)
    {
        using var response = await Management.GetAsync($"queues/%2F/{queue}");
        return response.IsSuccessStatusCode;
    }

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await Management.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Closes a host's connection from the broker side, as a broker restart or an operator would.</summary>
    public async Task ForceCloseConnectionAsync(string clientName)
    {
        // The management API lists a new connection a moment after it opens
        List<string> names = [];
        await WaitUntilAsync(async () =>
        {
            var connections = await GetJsonAsync("connections");
            names =
            [
                .. connections.EnumerateArray()
                    .Where(connection => connection.GetProperty("client_properties").TryGetProperty("connection_name", out var name)
                        && name.GetString() == clientName)
                    .Select(connection => connection.GetProperty("name").GetString()!),
            ];
            return names.Count > 0;
        }, $"RabbitMQ to list the connection {clientName}");

        foreach (var name in names)
        {
            using var response = await Management.DeleteAsync($"connections/{Uri.EscapeDataString(name)}");
            Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound, $"Closing {name}: {response.StatusCode}");
        }
    }

    public static Task WaitUntilAsync(Func<bool> condition, string what, int seconds = 30) =>
        WaitUntilAsync(() => Task.FromResult(condition()), what, seconds);

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out after {seconds} s waiting for {what}.");
            await Task.Delay(100);
        }
    }

    public static async Task<BasicGetResult> GetSoonAsync(IChannel channel, string queue, bool autoAck, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            if (await channel.BasicGetAsync(queue, autoAck) is { } message)
            {
                return message;
            }

            Assert.True(DateTime.UtcNow < deadline, $"No message arrived in {queue} within {seconds} s.");
            await Task.Delay(100);
        }
    }

    public static OutgoingMessage Event(string routingKey, string json = """{"orderId":"019a2180-0000-7000-8000-000000000001"}""") =>
        new(Guid.CreateVersion7(), routingKey, Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow);

    public static async Task<HealthStatus> HealthAsync(IHost host)
    {
        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        return report.Entries["rabbitmq"].Status;
    }

    public static string Header(IReadOnlyBasicProperties properties, string name) =>
        (properties.Headers is { } headers && headers.TryGetValue(name, out var value) ? value : null) switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            null => throw new Xunit.Sdk.XunitException($"The message has no {name} header."),
            var other => other.ToString()!,
        };

    // A test that breaks a channel rule (such as a passive declare of a missing queue) closes the channel
    private async Task EnsureProbeAsync()
    {
        if (!Probe.IsOpen)
        {
            await Probe.DisposeAsync();
            Probe = await _probe.CreateChannelAsync();
        }
    }
}
