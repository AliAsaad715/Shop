using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shop.Modules.Products.Data;
using Shop.SharedKernel.Diagnostics;

namespace Shop.Modules.Products.Catalog;

internal sealed class CachedCatalogReader(
    HybridCache cache, IServiceScopeFactory scopes, IOptions<CatalogCacheOptions> options, IHttpContextAccessor requests)
{
    public Task<ProductPageResponse> ListAsync(ListProductsRequest request, CancellationToken ct) =>
        ReadAsync(request.Cursor is null && (request.Page ?? 1) <= options.Value.MaxCachedPage
                ? CatalogCacheKeys.List(request) : null,
            options.Value.ListTtl,
            [CatalogCacheKeys.CatalogTag, request.Category is { } category
                ? CatalogCacheKeys.CategoryTag(category) : CatalogCacheKeys.AllProductsTag],
            (db, token) => CatalogQueries.ListAsync(request, db, token), ct);

    public Task<List<CategoryResponse>> CategoriesAsync(CancellationToken ct) =>
        ReadAsync(CatalogCacheKeys.Categories, options.Value.CategoriesTtl, [CatalogCacheKeys.CatalogTag],
            CatalogQueries.CategoriesAsync, ct);

    public async Task<ProductResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        try
        {
            return await ReadAsync(CatalogCacheKeys.Product(id), options.Value.ProductTtl, [CatalogCacheKeys.CatalogTag],
                async (db, token) => await CatalogQueries.GetAsync(id, db, token) ?? throw new MissingProductException(), ct);
        }
        catch (MissingProductException)
        {
            // HybridCache doesn't retain failed factories: unknown IDs cannot fill Redis with ten-minute nulls.
            return null;
        }
    }

    private async Task<T> ReadAsync<T>(string? key, TimeSpan ttl, string[] tags,
        Func<ProductsDbContext, CancellationToken, Task<T>> query, CancellationToken ct)
    {
        var state = new Fill<T>(scopes, requests.HttpContext?.Features.Get<DbQueryCount>(), query);
        if (!options.Value.Enabled || key is null) return await state.ReadAsync(ct);
        return await cache.GetOrCreateAsync(key, state, static (fill, token) => fill.ReadAsync(token),
            new HybridCacheEntryOptions { Expiration = ttl, LocalCacheExpiration = options.Value.LocalTtl }, tags, ct);
    }

    private sealed record Fill<T>(IServiceScopeFactory Scopes, DbQueryCount? Counter,
        Func<ProductsDbContext, CancellationToken, Task<T>> Query)
    {
        public async ValueTask<T> ReadAsync(CancellationToken ct)
        {
            using var diagnostic = Counter?.Activate();
            await using var scope = Scopes.CreateAsyncScope();
            return await Query(scope.ServiceProvider.GetRequiredService<ProductsDbContext>(), ct);
        }
    }

    private sealed class MissingProductException : Exception;
}
