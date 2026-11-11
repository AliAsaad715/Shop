using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Shop.SharedKernel.Diagnostics;

namespace Shop.SharedKernel.Caching;

/// <summary>A value read through the cache, and whether the cache had it.</summary>
public readonly record struct CacheRead<T>(T Value, bool Hit);

public static class CacheReads
{
    /// <summary>Says whether the cache answered a request: <c>HIT</c> or <c>MISS</c> (task R3-07).</summary>
    public const string HeaderName = "X-Cache";

    /// <summary>
    /// HybridCache's GetOrCreateAsync, which also says whether the value came from the cache. A request that waited
    /// while another loaded the same key (stampede protection) counts as a hit: it sent nothing to the database itself.
    /// </summary>
    public static async ValueTask<CacheRead<T>> GetOrLoadAsync<T>(
        this HybridCache cache, string key, Func<CancellationToken, ValueTask<T>> load,
        HybridCacheEntryOptions options, IEnumerable<string> tags, CancellationToken ct)
    {
        // HybridCache runs the load on the thread pool, apart from the request, so the load's SQL commands are
        // counted toward X-Db-Queries only through the request whose miss started it
        var count = DbQueryCount.Current.Value;
        var loaded = false;
        var value = await cache.GetOrCreateAsync(key, async token =>
        {
            loaded = true;
            DbQueryCount.Current.Value = count;
            return await load(token);
        }, options, tags, ct);

        return new CacheRead<T>(value, !loaded);
    }

    public static void MarkCache(this HttpResponse response, bool hit) => response.Headers[HeaderName] = hit ? "HIT" : "MISS";
}
