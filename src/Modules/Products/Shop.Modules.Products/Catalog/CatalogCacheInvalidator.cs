using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shop.Modules.Products.Catalog;

internal sealed class CatalogCacheInvalidator(
    HybridCache cache, IOptions<CatalogCacheOptions> options, ILogger<CatalogCacheInvalidator> logger)
{
    // Call after commit; an edit that moves a product must pass BOTH categories (R3-08).
    public Task ProductChangedAsync(Guid id, Guid? oldCategory, Guid? newCategory) =>
        InvalidateAsync([CatalogCacheKeys.Product(id)],
            new[] { oldCategory, newCategory }.Where(id => id.HasValue).Select(id => CatalogCacheKeys.CategoryTag(id!.Value))
                .Append(CatalogCacheKeys.AllProductsTag).Distinct().ToArray());

    public Task ProductsCreatedAsync(IEnumerable<(Guid Id, Guid CategoryId)> products)
    {
        var created = products.ToArray();
        return created.Length == 0 ? Task.CompletedTask : InvalidateAsync(
            created.Select(p => CatalogCacheKeys.Product(p.Id)).ToArray(),
            created.Select(p => CatalogCacheKeys.CategoryTag(p.CategoryId)).Append(CatalogCacheKeys.AllProductsTag).Distinct().ToArray());
    }

    public Task CategoriesChangedAsync() => InvalidateAsync([CatalogCacheKeys.Categories], [CatalogCacheKeys.CatalogTag]);

    private async Task InvalidateAsync(string[] keys, string[] tags)
    {
        if (!options.Value.Enabled) return;
        // The database has committed. Disconnecting the writer must not cancel invalidation.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Task.WhenAll(
            AttemptAsync(() => cache.RemoveAsync(keys, timeout.Token)),
            AttemptAsync(() => cache.RemoveByTagAsync(tags, timeout.Token)));
    }

    private async Task AttemptAsync(Func<ValueTask> invalidate)
    {
        try { await invalidate(); }
        catch (Exception error)
        {
            // This operation touches only the cache; never report a committed write as a database failure.
            logger.LogWarning(error, "Catalog cache invalidation failed after commit; entries remain bounded by their TTL");
        }
    }
}
