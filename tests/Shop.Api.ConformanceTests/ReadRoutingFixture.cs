using System.Net.Http.Headers;
using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Shop.Modules.Products.Contracts;
using Shop.SharedKernel.Auth;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Api.ConformanceTests;

// A real primary and physical streaming standby, independent of the developer's Compose databases/snapshots.
public sealed class ReadRoutingFixture : IAsyncLifetime
{
    private readonly INetwork _network = new NetworkBuilder().Build();
    private PostgreSqlContainer _primary = null!;
    private IContainer _replica = null!;
    private readonly IContainer _redis = new ContainerBuilder("redis:8.10.3-alpine")
        .WithPortBinding(6379, true).WithCommand("redis-server", "--save", "", "--appendonly", "no")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private readonly byte[] _jwtKey = RandomNumberGenerator.GetBytes(32);
    private readonly DirectoryInfo _images = Directory.CreateTempSubdirectory("shop-read-routing-");
    private readonly List<ReadRoutingApi> _hosts = [];
    private ConnectionMultiplexer _redisConnection = null!;

    public static readonly Guid Customer = Guid.Parse("019a2180-4000-7000-8000-000000000001");
    public static readonly Guid OtherCustomer = Guid.Parse("019a2180-4000-7000-8000-000000000002");
    public static readonly Guid Admin = Guid.Parse("019a2180-4000-7000-8000-000000000003");
    public HttpClient First { get; private set; } = null!;
    public HttpClient Second { get; private set; } = null!;
    public Guid Product { get; private set; }
    public Guid Category { get; private set; }
    public IDatabase Redis => _redisConnection.GetDatabase();
    public string PrimaryConnection => _primary.GetConnectionString();
    public string ReplicaConnection => new NpgsqlConnectionStringBuilder(PrimaryConnection)
        { Host = _replica.Hostname, Port = _replica.GetMappedPublicPort(5432) }.ConnectionString;

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();
        _primary = new PostgreSqlBuilder("postgres:18").WithDatabase("routing")
            .WithUsername("postgres").WithPassword("routing-test-password")
            .WithNetwork(_network).WithNetworkAliases("primary")
            .WithCommand("-c", "wal_level=replica", "-c", "max_wal_senders=10").Build();
        await Task.WhenAll(_primary.StartAsync(), _redis.StartAsync());
        _redisConnection = await ConnectionMultiplexer.ConnectAsync($"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)}");
        // Migrate all modules through the real application before copying its database.
        await using (var bootstrap = Build(new() { ["ReadRouting:Enabled"] = "false" }, readConnection: PrimaryConnection))
        using (var client = bootstrap.CreateClient()) { }
        await SqlAsync("""
            INSERT INTO products.products(id, name, description, price, image_path, category_id, created_at)
            SELECT md5('routing-product-' || n)::uuid, 'Routing product', 'Routing description', 10, 'products/test.png',
                (SELECT id FROM products.categories WHERE name = 'Books'), '2026-01-01'::timestamptz + n * interval '1 second'
            FROM generate_series(1,85) AS n;
            """);
        Product = Guid.Parse((await ScalarAsync(PrimaryConnection, "SELECT id::text FROM products.products ORDER BY created_at DESC LIMIT 1"))!);
        Category = Guid.Parse((await ScalarAsync(PrimaryConnection, "SELECT id::text FROM products.categories WHERE name='Books'"))!);
        var hba = await _primary.ExecAsync(["sh", "-c",
            "echo 'host replication postgres all trust' >> \"$PGDATA/pg_hba.conf\"; psql -U postgres -d routing -c 'SELECT pg_reload_conf()'"]);
        Assert.Equal(0, hba.ExitCode);
        _replica = new ContainerBuilder("postgres:18").WithNetwork(_network)
            .WithPortBinding(5432, true).WithEnvironment("PGDATA", "/var/lib/postgresql/18/docker")
            .WithEntrypoint("sh", "-c")
            .WithCommand("set -e; if [ ! -s \"$PGDATA/PG_VERSION\" ]; then mkdir -p \"$PGDATA\";"
                + " pg_basebackup -h primary -U postgres -D \"$PGDATA\" -R -X stream --checkpoint=fast;"
                + " chown -R postgres:postgres \"$PGDATA\"; chmod 700 \"$PGDATA\"; fi;"
                + " exec gosu postgres postgres -D \"$PGDATA\" -c hot_standby=on")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                "pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "routing")).Build();
        await _replica.StartAsync();
        Assert.Equal("true", await ScalarAsync(ReplicaConnection, "SELECT pg_is_in_recovery()::text"));
        await WaitForReplayAsync();
    }

    public async Task ResetAsync()
    {
        First?.Dispose(); Second?.Dispose();
        foreach (var host in _hosts) await host.DisposeAsync();
        _hosts.Clear();
        await _replica.StartAsync();
        await ResumeAsync();
        await SqlAsync($"""
            DELETE FROM orders.orders;
            DELETE FROM orders.pending_releases;
            DELETE FROM products.products WHERE image_path <> 'products/test.png';
            UPDATE products.products SET name='Routing product', description='Routing description', price=10;
            UPDATE products.categories SET name='Books' WHERE id='{Category}';
            """);
        await WaitForReplayAsync();
        await Redis.ExecuteAsync("FLUSHDB");
        First = NewHost(); Second = NewHost();
    }

    public HttpClient NewHost(Dictionary<string, string?>? settings = null)
    {
        var host = Build(settings);
        _hosts.Add(host);
        return host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://shop.test"), AllowAutoRedirect = false });
    }

    private ReadRoutingApi Build(Dictionary<string, string?>? overrides, string? readConnection = null)
    {
        var read = new NpgsqlConnectionStringBuilder(PrimaryConnection)
        {
            Host = $"{_replica?.Hostname ?? _primary.Hostname}:{(_replica is null ? _primary.GetMappedPublicPort(5432) : _replica.GetMappedPublicPort(5432))},{_primary.Hostname}:{_primary.GetMappedPublicPort(5432)}",
            Port = 5432, TargetSessionAttributes = "prefer-standby", Timeout = 1, HostRecheckSeconds = 1,
            ApplicationName = $"routing-read-{Guid.NewGuid():N}",
        };
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = PrimaryConnection,
            ["ConnectionStrings:ShopRead"] = readConnection ?? read.ConnectionString,
            ["ConnectionStrings:Redis"] = $"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)}",
            ["ReadRouting:Enabled"] = "true", ["ReadRouting:PrimaryWindow"] = "00:00:10",
            ["CatalogCache:Enabled"] = "true", ["DemoSeed:Enabled"] = "false",
            ["Images:RootPath"] = _images.FullName,
            ["Admin:Email"] = "", ["Admin:Password"] = "",
            ["Jwt:SigningKey"] = Convert.ToBase64String(_jwtKey),
            ["Jwt:Issuer"] = "shop", ["Jwt:Audience"] = "shop-api",
            ["LoginRateLimit:PermitLimit"] = "1000", ["LoginRateLimit:WindowSeconds"] = "60",
            ["Inventory:Address"] = "http://localhost:50051", ["Orders:PendingReleases:PollInterval"] = "01:00:00",
        };
        if (overrides is not null) foreach (var setting in overrides) settings[setting.Key] = setting.Value;
        return new ReadRoutingApi(settings);
    }

    public string Token(Guid user, string role) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "shop", Audience = "shop-api", IssuedAt = DateTime.UtcNow, NotBefore = DateTime.UtcNow,
        Expires = DateTime.UtcNow.AddMinutes(15), TokenType = "JWT",
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(_jwtKey), SecurityAlgorithms.HmacSha256),
        Claims = new Dictionary<string, object>
        {
            ["sub"] = user.ToString(), ["role"] = role, ["email"] = $"{user:N}@routing.test", ["jti"] = Guid.NewGuid().ToString(),
        },
    });

    public async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path,
        Guid? user = null, string role = Roles.Customer, HttpContent? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body };
        if (user is { } id) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(id, role));
        return await client.SendAsync(request);
    }

    public async Task PauseAsync()
    {
        await ScalarAsync(ReplicaConnection, "SELECT pg_wal_replay_pause()");
        await UntilAsync(async () => await ScalarAsync(ReplicaConnection, "SELECT pg_get_wal_replay_pause_state()") == "paused");
    }

    public Task ResumeAsync() => ScalarAsync(ReplicaConnection, "SELECT pg_wal_replay_resume()");
    public Task StopReplicaAsync() => _replica.StopAsync();
    public async Task WaitForReplayAsync()
    {
        // Testcontainers permits asynchronous commit; wait for the commit record inserted, not an older flush LSN.
        var lsn = await ScalarAsync(PrimaryConnection, "SELECT pg_current_wal_insert_lsn()::text");
        await UntilAsync(async () => await ScalarAsync(ReplicaConnection,
            $"SELECT (pg_last_wal_replay_lsn() >= '{lsn}'::pg_lsn)::text") == "true");
    }

    public async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(PrimaryConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    public static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition()) await Task.Delay(25, timeout.Token);
    }

    public async Task DisposeAsync()
    {
        First?.Dispose(); Second?.Dispose();
        foreach (var host in _hosts) await host.DisposeAsync();
        _redisConnection?.Dispose();
        if (_replica is not null) await _replica.DisposeAsync();
        if (_primary is not null) await _primary.DisposeAsync();
        await _redis.DisposeAsync(); await _network.DisposeAsync();
        if (_images.FullName.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && _images.Name.StartsWith("shop-read-routing-", StringComparison.Ordinal)) _images.Delete(true);
    }
}

internal sealed class ReadRoutingApi(Dictionary<string, string?> settings) : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IInventoryService>();
            services.AddSingleton<IInventoryService, RoutingInventory>();
        });
    }
}

internal sealed class RoutingInventory : IInventoryService
{
    public Task<int> GetAvailableAsync(Guid productId, CancellationToken ct) => Task.FromResult(100);
    public Task<ReserveResult> ReserveAsync(Guid orderId, IReadOnlyList<StockLine> lines, CancellationToken ct) => Task.FromResult(new ReserveResult(true));
    public Task ReleaseAsync(Guid orderId, CancellationToken ct) => Task.CompletedTask;
}
