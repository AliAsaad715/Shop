using System.Globalization;
using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Shop.Modules.Users.Auth;

internal sealed class LoginRateLimitPolicy(IOptions<LoginRateLimitOptions> options, ILogger<LoginRateLimitPolicy> logger)
    : IRateLimiterPolicy<string>
{
    public const string Name = "users.login";
    private static readonly object PartitionLogged = new();

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        var address = httpContext.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true)
        {
            address = address.MapToIPv4();
        }

        // The host has already applied its trusted-proxy policy; never read raw forwarding headers here.
        var key = address?.ToString() ?? "unknown";
        // Rejected requests can ask for a partition twice (immediate acquisition, then queue acquisition).
        // The marker belongs to this request only; no quota or client state is kept in the static key.
        if (httpContext.Items.TryAdd(PartitionLogged, true))
        {
            logger.LogInformation("Login rate-limit partition {ClientIp} trace {TraceId}", key,
                Activity.Current?.Id ?? httpContext.TraceIdentifier);
        }
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new()
        {
            PermitLimit = options.Value.PermitLimit,
            Window = TimeSpan.FromSeconds(options.Value.WindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => (context, _) =>
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.CacheControl = "no-store";
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
        }

        // The host's status-code middleware supplies the shared Problem Details contract.
        return ValueTask.CompletedTask;
    };
}
