using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shop.Modules.Products.Data;
using Shop.SharedKernel.Caching;

namespace Shop.Modules.Products.Catalog;

/// <summary>
/// What the catalog caches, for how long, and what clears it (task R3-07). Cache-aside: a read takes the cached copy,
/// or loads it from the database and keeps it; a write changes the database, then removes what it changed, so an
/// edit shows at once, and the TTLs only catch what a write forgot. Only what is the same for everyone is cached.
/// Never cached: stock, which every order changes and the warehouse decides, and the prices an order is placed at,
/// which <see cref="ProductCatalog"/> reads from the database.
/// </summary>
internal sealed class CatalogCache(HybridCache cache, IServiceScopeFactory scopes, ILogger<CatalogCache> logger)
{
    // Product details without their stock: read constantly, changed rarely
    private static readonly HybridCacheEntryOptions Product = Entry(TimeSpan.FromMinutes(10));

    // Categories almost never change
    private static readonly HybridCacheEntryOptions Categories = Entry(TimeSpan.FromHours(1));

    // The first page of each list, where most browsing stops
    private static readonly HybridCacheEntryOptions FirstPage = Entry(TimeSpan.FromSeconds(60));

    // Keys and tags start with this module's build: Redis keeps entries across a deploy, and a new build mustn't read
    // what an older one cached, whose types or categories may differ
    private static readonly string Build = typeof(CatalogCache).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];

    // On every entry, so a change to the categories clears everything at once
    private static readonly string CatalogTag = $"{Build}:catalog";

    public ValueTask<CacheRead<CachedProduct?>> ProductAsync(
        Guid id, Func<ProductsDbContext, CancellationToken, Task<CachedProduct?>> load, CancellationToken ct) =>
        ReadAsync(ProductKey(id), load, Product, [CatalogTag], ct);

    public ValueTask<CacheRead<CategoryList>> CategoriesAsync(
        Func<ProductsDbContext, CancellationToken, Task<CategoryList>> load, CancellationToken ct) =>
        ReadAsync($"{Build}:categories", load, Categories, [CatalogTag], ct);

    // A first page also carries its list's tag, so a product's change clears only the lists it's in
    public ValueTask<CacheRead<ProductPageResponse>> FirstPageAsync(
        Guid? category, int pageSize, Func<ProductsDbContext, CancellationToken, Task<ProductPageResponse>> load,
        CancellationToken ct) =>
        ReadAsync($"{Build}:products:{List(category)}:page1:size{pageSize}", load, FirstPage, [CatalogTag, ListTag(category)], ct);

    /// <summary>
    /// After a product is created or edited: its details, and the first pages of the lists it was or now is in, its
    /// categories' and the whole catalog's.
    /// </summary>
    public Task ProductChangedAsync(Guid productId, params IEnumerable<Guid> categoryIds) => ClearAsync(async () =>
    {
        await cache.RemoveAsync(ProductKey(productId), CancellationToken.None);
        await cache.RemoveByTagAsync([ListTag(null), .. categoryIds.Select(id => ListTag(id))], CancellationToken.None);
    });

    /// <summary>After the categories change: everything, since product details and lists show category names.</summary>
    public Task CatalogChangedAsync() => ClearAsync(() => cache.RemoveByTagAsync(CatalogTag, CancellationToken.None));

    // A load gets its own scope and DbContext: other requests may be waiting on it (stampede protection), so it
    // mustn't end with the request that started it
    private ValueTask<CacheRead<T>> ReadAsync<T>(
        string key, Func<ProductsDbContext, CancellationToken, Task<T>> load, HybridCacheEntryOptions options,
        IEnumerable<string> tags, CancellationToken ct) =>
        cache.GetOrLoadAsync(key, async token =>
        {
            await using var scope = scopes.CreateAsyncScope();
            return await load(scope.ServiceProvider.GetRequiredService<ProductsDbContext>(), token);
        }, options, tags, ct);

    // The write has reached the database, so a cache that can't be cleared mustn't fail it: what's left expires with
    // its TTL. For the same reason clearing never stops with the request
    private async Task ClearAsync(Func<ValueTask> clear)
    {
        try
        {
            await clear();
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Could not clear cached catalog entries; they expire with their TTL");
        }
    }

    private static string ProductKey(Guid id) => $"{Build}:product:{id}";

    private static string List(Guid? category) => category?.ToString() ?? "all";

    private static string ListTag(Guid? category) => $"{Build}:list:{List(category)}";

    private static HybridCacheEntryOptions Entry(TimeSpan expiration) => new()
    {
        Expiration = expiration,
        // This instance's own copy stays short: removing a key reaches Redis and this instance, but another instance
        // keeps its copy until it expires, and Requirement 5 runs several
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };
}
