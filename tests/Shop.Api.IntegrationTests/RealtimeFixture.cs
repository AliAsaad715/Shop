using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Testcontainers.RabbitMq;
using Xunit;

namespace Shop.Api.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class RealtimeCollection : ICollectionFixture<RealtimeFixture>
{
    public const string Name = "Realtime RabbitMQ";
}

public sealed class RealtimeFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4.3.7-management-alpine")
        .WithUsername("shop").WithPassword("shop").WithPortBinding(15672, true).Build();
    private HttpClient _management = null!;

    public Dictionary<string, string?> Settings => new()
    {
        ["RabbitMq:Host"] = _broker.Hostname,
        ["RabbitMq:Port"] = _broker.GetMappedPublicPort(5672).ToString(),
        ["RabbitMq:UserName"] = "shop",
        ["RabbitMq:Password"] = "shop",
        ["RabbitMq:ConnectTimeout"] = "00:00:01",
    };

    public async Task InitializeAsync()
    {
        await _broker.StartAsync();
        _management = new HttpClient
        {
            BaseAddress = new Uri($"http://{_broker.Hostname}:{_broker.GetMappedPublicPort(15672)}/api/"),
        };
        _management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("shop:shop")));
    }

    public async Task DisposeAsync()
    {
        _management?.Dispose();
        await _broker.DisposeAsync();
    }

    public async Task<JsonElement> JsonAsync(string path)
    {
        using var response = await _management.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<bool> QueueExistsAsync(string queue)
    {
        using var response = await _management.GetAsync($"queues/%2F/{Uri.EscapeDataString(queue)}");
        return response.IsSuccessStatusCode;
    }

    public async Task CloseConnectionAsync(string clientName)
    {
        string[] names = [];
        await WaitAsync(async () =>
        {
            var connections = await JsonAsync("connections");
            names = connections.EnumerateArray()
                .Where(connection => connection.GetProperty("client_properties").TryGetProperty("connection_name", out var name)
                    && name.GetString() == clientName)
                .Select(connection => connection.GetProperty("name").GetString()!).ToArray();
            return names.Length > 0;
        }, "connection to appear in management API");
        foreach (var name in names)
        {
            using var response = await _management.DeleteAsync($"connections/{Uri.EscapeDataString(name)}");
            Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
        }
    }

    public static async Task WaitAsync(Func<Task<bool>> condition, string description, int seconds = 30)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!await condition())
        {
            Assert.False(timeout.IsCancellationRequested, $"Timed out waiting for {description}");
            await Task.Delay(100);
        }
    }

    public static Task WaitAsync(Func<bool> condition, string description) =>
        WaitAsync(() => Task.FromResult(condition()), description);
}
