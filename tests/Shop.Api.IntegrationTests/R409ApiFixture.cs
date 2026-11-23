using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;
using Shop.Messaging;
using Shop.Modules.Notifications.Realtime;
using Shop.Modules.Products.Contracts;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Persistence;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;
using Xunit.Abstractions;

namespace Shop.Api.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class R409Collection : ICollectionFixture<R409ApiFixture>
{
    public const string Name = "R4-09 full API";
}

// Private containers for this collection, separate from compose and the smaller SignalR tests.
public sealed class R409ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4.3.7-management-alpine")
        .WithUsername("shop").WithPassword("shop").WithPortBinding(15672, true).Build();
    private IConnection _probeConnection = null!;
    private IChannel _probe = null!;
    private HttpClient _management = null!;

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await Task.WhenAll(_postgres.StartAsync(timeout.Token), _broker.StartAsync(timeout.Token));
        _probeConnection = await new ConnectionFactory
        {
            Uri = new Uri(_broker.GetConnectionString()), ClientProvidedName = "r409-probe",
        }.CreateConnectionAsync(timeout.Token);
        _probe = await _probeConnection.CreateChannelAsync(cancellationToken: timeout.Token);
        _management = new HttpClient
        {
            BaseAddress = new Uri($"http://{_broker.Hostname}:{_broker.GetMappedPublicPort(15672)}/api/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        _management.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("shop:shop")));
    }

    internal async Task<R409ApiFactory> CreateApiAsync(CancellationToken ct)
    {
        // Each test gets one database containing all module schemas. A failed test cannot leave outbox work for
        // the next API host to publish; all databases disappear with the collection's PostgreSQL container.
        var database = $"r409_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync(ct);
        await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
        await create.ExecuteNonQueryAsync(ct); // The identifier is generated above, never supplied by a caller.
        var connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ToString();
        return new R409ApiFactory(connectionString, new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = _broker.Hostname,
            ["RabbitMq:Port"] = _broker.GetMappedPublicPort(5672).ToString(),
            ["RabbitMq:UserName"] = "shop",
            ["RabbitMq:Password"] = "shop",
            ["RabbitMq:ConnectTimeout"] = "00:00:01",
        });
    }

    internal async Task ResetQueuesAsync(CancellationToken ct)
    {
        // The previous test has disposed its API; close the probe channel to return any unacknowledged probes.
        await _probe.DisposeAsync();
        _probe = await _probeConnection.CreateChannelAsync(cancellationToken: ct);
        await _probe.QueuePurgeAsync(ShopTopology.EmailQueue, ct);
        await _probe.QueuePurgeAsync(ShopTopology.EmailDeadLetterQueue, ct);
    }

    internal async Task<BasicGetResult> EmailAsync(CancellationToken ct)
    {
        BasicGetResult? delivery = null;
        await WaitAsync(async token =>
        {
            if (!_probe.IsOpen) return false;
            delivery = await _probe.BasicGetAsync(ShopTopology.EmailQueue, autoAck: true, token);
            return delivery is not null;
        }, "the durable email copy", ct);
        return delivery!;
    }

    internal async Task BrokerApplicationAsync(bool start, CancellationToken ct)
    {
        var result = await _broker.ExecAsync(["rabbitmqctl", start ? "start_app" : "stop_app"], ct);
        Assert.Equal(0, result.ExitCode);
    }

    internal async Task DiagnosticsAsync(ITestOutputHelper output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            foreach (var path in new[] { $"queues/%2F/{ShopTopology.EmailQueue}", $"queues/%2F/{ShopTopology.EmailDeadLetterQueue}", "connections" })
                output.WriteLine($"{path}: {await _management.GetStringAsync(path, timeout.Token)}");
            var brokerLogs = await _broker.GetLogsAsync(ct: timeout.Token);
            output.WriteLine(string.Join(Environment.NewLine, brokerLogs.Stdout.Split('\n').TakeLast(60)));
            output.WriteLine(brokerLogs.Stderr);
            var databaseLogs = await _postgres.GetLogsAsync(ct: timeout.Token);
            output.WriteLine(string.Join(Environment.NewLine, databaseLogs.Stdout.Split('\n').TakeLast(20)));
            output.WriteLine(databaseLogs.Stderr);
        }
        catch (Exception error)
        {
            output.WriteLine($"Diagnostics unavailable: {error.Message}");
        }
    }

    internal static async Task WaitAsync(Func<CancellationToken, Task<bool>> condition, string description, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (!await condition(timeout.Token)) await Task.Delay(100, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {description}.");
        }
    }

    public async Task DisposeAsync()
    {
        _management?.Dispose();
        if (_probe is not null) await _probe.DisposeAsync();
        if (_probeConnection is not null) await _probeConnection.DisposeAsync();
        await _broker.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

internal sealed class R409ApiFactory(string connectionString, Dictionary<string, string?> messagingSettings)
    : WebApplicationFactory<global::Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = builder.Build();
        host.InitializeModuleDatabasesAsync().GetAwaiter().GetResult();
        host.Start();
        return host;
    }

    internal static readonly Uri BaseAddress = new("https://shop.test");
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly SignalRApi.ConnectedConnections _connections = new();

    internal bool IsReady => Services.GetRequiredService<LiveUpdateState>().IsReady;
    internal string Token(Guid userId, string role = Roles.Customer) => SignalRApi.CreateToken(_key, userId, role);
    internal HubConnection Connection(string path, Guid userId, string role = Roles.Customer) =>
        SignalRApi.Connection(Server, BaseAddress, path, Token(userId, role));

    internal async Task StartAsync(HubConnection connection, CancellationToken ct)
    {
        await connection.StartAsync(ct);
        await _connections.WaitAsync(connection.ConnectionId!, ct);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>(messagingSettings)
            {
                ["ConnectionStrings:Shop"] = connectionString,
                ["ReadRouting:Enabled"] = "false",
                ["CatalogCache:Enabled"] = "false",
                ["DemoSeed:Enabled"] = "false",
                ["Admin:Email"] = string.Empty,
                ["Admin:Password"] = string.Empty,
                ["AllowedHosts"] = "*",
                ["Images:RootPath"] = Path.Combine(Path.GetTempPath(), $"shop-r409-images-{Guid.NewGuid():N}"),
                ["Jwt:SigningKey"] = Convert.ToBase64String(_key),
                ["Jwt:Issuer"] = "shop",
                ["Jwt:Audience"] = "shop-api",
                ["Jwt:LifetimeMinutes"] = "15",
                ["LoginRateLimit:PermitLimit"] = "5",
                ["LoginRateLimit:WindowSeconds"] = "60",
                ["Inventory:Address"] = "http://127.0.0.1:50051",
                ["Orders:Outbox:PollInterval"] = "00:00:01",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProductCatalog>();
            services.AddSingleton<IProductCatalog, R409Catalog>();
            services.RemoveAll<IInventoryService>();
            services.AddSingleton<IInventoryService, AvailableInventory>();
            services.AddSingleton(_connections);
            services.AddSignalR(options => options.AddFilter<SignalRApi.ConnectedConnections>());
        });
    }

    internal async Task<OutboxRow> OutboxAsync(Guid orderId, string type, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT m.id, m.type, m.payload::text, m.published_at, m.attempts
            FROM orders.outbox_messages m
            JOIN orders.orders o ON o.id = @orderId
            WHERE m.payload->>'orderId' = @orderText AND m.type = @type
            """, connection);
        command.Parameters.AddWithValue("orderId", orderId);
        command.Parameters.AddWithValue("orderText", orderId.ToString());
        command.Parameters.AddWithValue("type", type);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct), $"Missing order or {type} outbox row for {orderId}.");
        var row = new OutboxRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3), reader.GetInt32(4));
        Assert.False(await reader.ReadAsync(ct), $"Multiple {type} rows for {orderId}.");
        return row;
    }

    internal sealed record OutboxRow(Guid Id, string Type, string Payload, DateTime? PublishedAt, int Attempts);

    internal sealed class R409Catalog : IProductCatalog
    {
        internal static readonly ProductCatalogItem Book = new(Guid.Parse("019a2180-2000-7000-8000-000000000001"), "Book", 19.95m);

        public Task<IReadOnlyDictionary<Guid, ProductCatalogItem>> GetByIdsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ProductCatalogItem>>(
                productIds.Contains(Book.Id) ? new Dictionary<Guid, ProductCatalogItem> { [Book.Id] = Book } : new Dictionary<Guid, ProductCatalogItem>());
    }

    internal sealed class AvailableInventory : IInventoryService
    {
        public Task<int> GetAvailableAsync(Guid productId, CancellationToken ct) => Task.FromResult(int.MaxValue);
        public Task<ReserveResult> ReserveAsync(Guid orderId, IReadOnlyList<StockLine> lines, CancellationToken ct) =>
            Task.FromResult(new ReserveResult(success: true));
        public Task ReleaseAsync(Guid orderId, CancellationToken ct) => Task.CompletedTask;
    }
}
