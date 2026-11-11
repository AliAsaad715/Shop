using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Shop.SharedKernel.Caching;

public static class CacheRegistration
{
    public const string RedisPrefix = "shop:r3:v1:";

    public static IServiceCollection AddShopCaching(this IServiceCollection services)
    {
        services.AddStackExchangeRedisCache(_ => { });
        services.AddOptions<RedisCacheOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            var redis = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis") ?? "localhost:6379");
            redis.AbortOnConnectFail = false;
            redis.ConnectRetry = 0;
            redis.ConnectTimeout = 500;
            redis.AsyncTimeout = 500;
            redis.SyncTimeout = 500;
            redis.BacklogPolicy = BacklogPolicy.FailFast;
            options.ConfigurationOptions = redis;
            options.InstanceName = RedisPrefix;
        });
        services.AddHybridCache();
        return services;
    }
}
