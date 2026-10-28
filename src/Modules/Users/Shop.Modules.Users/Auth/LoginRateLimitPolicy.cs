using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Shop.Modules.Users.Auth;

internal sealed class LoginRateLimitPolicy(IOptions<LoginRateLimitOptions> options) : IRateLimiterPolicy<string>
{
    public const string Name = "users.login";

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        var address = httpContext.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true)
        {
            address = address.MapToIPv4();
        }

        // Only trust the connection address, never client-supplied forwarding headers.
        return RateLimitPartition.GetFixedWindowLimiter(address?.ToString() ?? "unknown", _ => new()
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
