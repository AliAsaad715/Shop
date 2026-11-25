using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Shop.Modules.Users.Auth;

internal sealed class RedisLoginRateLimiter(
    string clientIp, LoginRateLimitOptions limits, LoginRateLimitStore store, ILogger logger) : RateLimiter
{
    internal const string Unavailable = "redis-unavailable";
    private static readonly RateLimitLease Deferred = new LoginLease(false);
    private long _lastAcquisition = Stopwatch.GetTimestamp();
    private int _activeAcquisitions;

    // Partition objects may be reclaimed when idle; recreating one never resets its Redis quota.
    public override TimeSpan? IdleDuration => Volatile.Read(ref _activeAcquisitions) == 0
        ? Stopwatch.GetElapsedTime(Volatile.Read(ref _lastAcquisition)) : null;
    public override RateLimiterStatistics? GetStatistics() => null;

    // The ASP.NET middleware tries synchronously first. Defer to AcquireAsync, so each request makes exactly
    // one Redis operation without blocking a thread or keeping a second, local quota.
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Deferred;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, limits.PermitLimit);
        Interlocked.Increment(ref _activeAcquisitions);
        try
        {
            var result = await store.AcquireAsync(clientIp, permitCount, limits, cancellationToken);
            return new LoginLease(result.Allowed, result.Allowed ? null : result.RetryAfter);
        }
        catch (RedisException exception)
        {
            // Losing the shared counter must not silently grant each host a new local allowance.
            logger.LogWarning(exception, "Login rate-limit Redis unavailable for {ClientIp}", clientIp);
            return new LoginLease(false, TimeSpan.FromSeconds(1), unavailable: true);
        }
        finally
        {
            Volatile.Write(ref _lastAcquisition, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref _activeAcquisitions);
        }
    }

    private sealed class LoginLease(bool acquired, TimeSpan? retryAfter = null, bool unavailable = false) : RateLimitLease
    {
        public override bool IsAcquired => acquired;
        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name, Unavailable];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = metadataName == MetadataName.RetryAfter.Name ? retryAfter
                : metadataName == Unavailable && unavailable ? true : null;
            return metadata is not null;
        }
    }
}
