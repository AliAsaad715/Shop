using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shop.SharedKernel.Persistence;

/// <summary>Only explicitly eligible reads use this router. Ordinary module contexts always use the primary.</summary>
public sealed class DatabaseReadRouter(
    IOptions<ReadRoutingOptions> options, IHttpContextAccessor requests, IMemoryCache local,
    IServiceProvider services, ILogger<DatabaseReadRouter> logger)
{
    public bool Enabled => options.Value.Enabled;
    public static string MarkerKey(Guid userId) => $"rw:{userId:D}";

    private Guid? UserId => requests.HttpContext?.User is { Identity.IsAuthenticated: true } user
        && Guid.TryParse(user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && id != Guid.Empty ? id : null;

    public async Task<DatabaseReadRoute> RouteAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enabled) return DatabaseReadRoute.Primary;
        if (UserId is not { } id) return DatabaseReadRoute.Replica;
        var key = MarkerKey(id);
        if (local.TryGetValue(key, out _)) return DatabaseReadRoute.Primary;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            return await services.GetRequiredService<IDistributedCache>().GetAsync(key, timeout.Token) is null
                ? DatabaseReadRoute.Replica : DatabaseReadRoute.Primary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning(error, "Cannot check read-your-writes marker; reading from the primary");
            return DatabaseReadRoute.Primary;
        }
    }

    /// <summary>Call after the database commit, before returning a write response; never use the client's token.</summary>
    public async Task MarkWrittenAsync()
    {
        if (!Enabled || UserId is not { } id) return;
        var key = MarkerKey(id);
        // Also protect this host if Redis fails during the write and recovers before the next read.
        local.Set(key, true, options.Value.PrimaryWindow);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await services.GetRequiredService<IDistributedCache>().SetAsync(key, [1],
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Value.PrimaryWindow }, timeout.Token);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Read-your-writes marker failed after commit; this host retains its local primary window");
        }
    }
}
