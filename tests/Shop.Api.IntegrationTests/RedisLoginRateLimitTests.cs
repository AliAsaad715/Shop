using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shop.SharedKernel.Errors;
using StackExchange.Redis;
using Xunit;

namespace Shop.Api.IntegrationTests;

// The real Program pipeline, three independent DI containers, and one real Redis. Empty credentials avoid DB I/O.
public sealed class RedisLoginRateLimitTests(ForwardedHeadersFixture fixture) : IClassFixture<ForwardedHeadersFixture>
{
    private const string Peer = "172.30.50.20";

    [Fact]
    public async Task TenAttemptsAcrossThreeInstancesShareFivePermits()
    {
        await using var api1 = Host("api1");
        await using var api2 = Host("api2");
        await using var api3 = Host("api3");
        ForwardedHeadersFactory[] hosts = [api1, api2, api3];
        var client = NewClient();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var response = await LoginAsync(hosts[attempt % 3], client);
            Assert.Equal($"api{attempt % 3 + 1}", response.Response.Headers["X-Instance"]);
            await AssertProblemAsync(response, attempt < 5 ? 401 : 429);
        }

        // Another real client behind the same proxy has its own counter.
        await AssertProblemAsync(await LoginAsync(api3, NewClient()), 401);
    }

    [Fact]
    public async Task ConcurrentAttemptsAcrossThreeInstancesCannotOverspendTheQuota()
    {
        await using var api1 = Host("api1");
        await using var api2 = Host("api2");
        await using var api3 = Host("api3");
        ForwardedHeadersFactory[] hosts = [api1, api2, api3];
        // Warm each host without consuming this client's permits.
        foreach (var host in hosts) await host.SendAsync(Peer, null, null);
        var client = NewClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(n => LoginAsync(hosts[n % 3], client)));
        Assert.Equal(5, responses.Count(context => context.Response.StatusCode == 401));
        Assert.Equal(25, responses.Count(context => context.Response.StatusCode == 429));
        foreach (var response in responses) await AssertProblemAsync(response, response.Response.StatusCode);
    }

    [Fact]
    public async Task RestartingAnApiDoesNotResetTheSharedQuota()
    {
        var client = NewClient();
        await using (var first = Host("api1"))
        {
            for (var n = 0; n < 5; n++) await AssertProblemAsync(await LoginAsync(first, client), 401);
        }
        await using var restarted = Host("api1");
        await AssertProblemAsync(await LoginAsync(restarted, client), 429);
    }

    [Fact]
    public async Task RejectedAttemptsDoNotExtendTheRedisWindowAndExpiryRestoresPermits()
    {
        await using var api = Host("api1");
        var client = NewClient();
        for (var n = 0; n < 5; n++) await LoginAsync(api, client);
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnection);
        var db = redis.GetDatabase();
        var key = "shop:users:login:v1:" + client;
        var initialTtl = await db.KeyTimeToLiveAsync(key);
        Assert.NotNull(initialTtl);
        await Task.Delay(50);
        await AssertProblemAsync(await LoginAsync(api, client), 429);
        Assert.True(await db.KeyTimeToLiveAsync(key) < initialTtl);
        Assert.Equal("5", (string?)await db.StringGetAsync(key));

        // Advance only this key's expiry rather than waiting a minute or flushing unrelated test data.
        Assert.True(await db.KeyExpireAsync(key, TimeSpan.FromMilliseconds(1)));
        await Task.Delay(50);
        await AssertProblemAsync(await LoginAsync(api, client), 401);
    }

    [Fact]
    public async Task RedisOutageFailsClosedWith503WhileOtherEndpointsStillWork()
    {
        await using var api = new ForwardedHeadersFactory(redisConnection: "127.0.0.1:1");
        var login = await LoginAsync(api, NewClient());
        await AssertProblemAsync(login, 503);
        Assert.Equal("1", login.Response.Headers.RetryAfter);
        var health = await api.SendAsync(Peer, null, null);
        Assert.Equal(StatusCodes.Status200OK, health.Response.StatusCode);
    }

    private ForwardedHeadersFactory Host(string name) => new(redisConnection: fixture.RedisConnection, instanceName: name);
    private static string NewClient() => new IPAddress(Guid.NewGuid().ToByteArray()).ToString();
    private static Task<HttpContext> LoginAsync(ForwardedHeadersFactory host, string client) =>
        host.SendAsync(Peer, client, "http", path: "/api/v1/auth/login");

    private static async Task AssertProblemAsync(HttpContext context, int status)
    {
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var problem = document.RootElement;
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        var code = status == 401 ? ErrorCodes.Users.InvalidCredentials : ErrorCodes.ForStatus(status);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        if (status is 429 or 503)
            Assert.InRange(int.Parse(context.Response.Headers.RetryAfter.ToString()), 1, 60);
    }
}
