using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Shop.Testing;

// A separate Redis per test host/fixture, unless a local test server was explicitly supplied.
internal sealed class RedisTestServer : IAsyncDisposable
{
    private IContainer? _container;
    public string ConnectionString { get; private set; } = null!;

    public async Task StartAsync()
    {
        var external = Environment.GetEnvironmentVariable("SHOP_TEST_REDIS_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
            return;
        }

        _container = new ContainerBuilder("redis:8.10.3-alpine")
            .WithPortBinding(6379, true)
            .WithCommand("redis-server", "--save", "", "--appendonly", "no", "--maxmemory-policy", "noeviction")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await _container.StartAsync();
        ConnectionString = $"{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}
