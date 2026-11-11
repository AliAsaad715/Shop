using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Shop.SharedKernel.Caching;

public static class ModuleCacheExtensions
{
    public const string RedisConnectionName = "Redis";

    /// <summary>
    /// HybridCache for a module (task R3-07): a copy in this process's memory, with Redis behind it once the host adds
    /// <see cref="AddRedisCache"/>. Every module may call it; the registration happens once.
    /// </summary>
    public static IServiceCollection AddModuleCache(this IServiceCollection services)
    {
        services.AddHybridCache();
        return services;
    }

    /// <summary>
    /// Puts Redis behind HybridCache when <c>ConnectionStrings:Redis</c> is set; without it, each process keeps only its
    /// own copy. A cache that is down or slow must cost a request little, so while Redis is unreachable a command fails
    /// at once instead of waiting in a queue, any command gives up after half a second, and the client keeps
    /// reconnecting in the background. HybridCache counts such a failure as a miss and reads the database.
    /// </summary>
    public static IServiceCollection AddRedisCache(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString(RedisConnectionName);
        if (string.IsNullOrWhiteSpace(connection))
        {
            return services;
        }

        var redis = ConfigurationOptions.Parse(connection);
        redis.AbortOnConnectFail = false;
        redis.BacklogPolicy = BacklogPolicy.FailFast;
        redis.ConnectTimeout = 2000;
        redis.AsyncTimeout = 500;
        redis.SyncTimeout = 500;
        services.AddStackExchangeRedisCache(options =>
        {
            options.ConfigurationOptions = redis;
            options.InstanceName = "shop:";
        });

        return services;
    }
}
