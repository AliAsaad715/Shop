using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Shop.Modules.Users.Auth;

// One connection per host, created only on the first login: the migrator never needs Redis.
internal sealed class LoginRateLimitStore(IConfiguration configuration) : IAsyncDisposable
{
    internal const string KeyPrefix = "shop:users:login:v1:";
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection = new(() =>
    {
        var options = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis") ?? "localhost:6379");
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 0;
        options.ConnectTimeout = 500;
        options.AsyncTimeout = 500;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        return ConnectionMultiplexer.ConnectAsync(options);
    });

    // The counter and its expiry change atomically. Redis owns the clock; hosts need no synchronized windows.
    // Denied attempts neither grow the counter nor extend the window. No quota is held in the API process.
    private const string AcquireScript = """
        local count = tonumber(redis.call('GET', KEYS[1])) or 0
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl < 0 then ttl = tonumber(ARGV[3]) end
        if count + tonumber(ARGV[1]) > tonumber(ARGV[2]) then
            return {0, ttl}
        end
        if tonumber(ARGV[1]) > 0 then
            redis.call('INCRBY', KEYS[1], ARGV[1])
            if redis.call('PTTL', KEYS[1]) < 0 then
                redis.call('PEXPIRE', KEYS[1], ARGV[3])
            end
        end
        return {1, ttl}
        """;

    internal async Task<(bool Allowed, TimeSpan RetryAfter)> AcquireAsync(
        string clientIp, int permits, LoginRateLimitOptions limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await _connection.Value.WaitAsync(cancellationToken);
        var result = (RedisResult[])(await connection.GetDatabase().ScriptEvaluateAsync(AcquireScript,
            [KeyPrefix + clientIp], [permits, limits.PermitLimit, limits.WindowSeconds * 1000L])
            .WaitAsync(cancellationToken))!;
        return ((long)result[0] == 1, TimeSpan.FromMilliseconds(Math.Max(1, (long)result[1])));
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated)
        {
            await (await _connection.Value).DisposeAsync();
        }
    }
}
