using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shop.Modules.Users.Auth;
using Xunit;

namespace Shop.Modules.Users.UnitTests;

public sealed class RedisLoginRateLimiterTests
{
    [Fact]
    public async Task UnreachableRedisRejectsAcquisitionAndPolicyReturns503WithRetryAfter()
    {
        await using var store = Store();
        var limits = new LoginRateLimitOptions();
        using var limiter = new RedisLoginRateLimiter("192.0.2.1", limits, store, NullLogger.Instance);
        using var lease = await limiter.AcquireAsync();
        Assert.False(lease.IsAcquired);
        Assert.True(lease.TryGetMetadata(RedisLoginRateLimiter.Unavailable, out _));
        Assert.True(lease.TryGetMetadata(MetadataName.RetryAfter, out var retry));
        Assert.Equal(TimeSpan.FromSeconds(1), retry);

        var policy = new LoginRateLimitPolicy(Options.Create(limits), store, NullLogger<LoginRateLimitPolicy>.Instance);
        var context = new DefaultHttpContext();
        await policy.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = lease }, CancellationToken.None);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("1", context.Response.Headers.RetryAfter);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task CanceledAcquisitionPropagatesCancellationRatherThanAQuotaOrOutageRejection()
    {
        await using var store = Store();
        using var limiter = new RedisLoginRateLimiter("192.0.2.2", new(), store, NullLogger.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var lease = await limiter.AcquireAsync(cancellationToken: cancellation.Token);
        });
    }

    private static LoginRateLimitStore Store() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = "127.0.0.1:1" }).Build());
}
