using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Products.Catalog;
using Shop.Modules.Products.Data;
using Shop.SharedKernel.Caching;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

// Task R3-07: what the catalog caches, and what clears it. Every test starts from an empty cache. This host has no
// Redis, so it tests HybridCache's in-memory layer; RedisCacheTests puts Redis behind it
[Collection("Products database")]
public sealed class CatalogCacheTests(ProductCatalogFixture fixture) : IClassFixture<ProductCatalogFixture>, IAsyncLifetime
{
    private static readonly Guid BookId = ProductCatalogFixture.SecondBookId;

    private CatalogCache Cache => fixture.App.Services.GetRequiredService<CatalogCache>();

    public Task InitializeAsync() => Cache.CatalogChangedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ProductDetailsComeFromTheCacheButTheStockNeverDoes()
    {
        fixture.Inventory.Stock[BookId] = 7;
        var (missed, before) = await ReadAsync($"/api/v1/products/{BookId}");
        fixture.Inventory.Stock[BookId] = 6;
        var (hit, after) = await ReadAsync($"/api/v1/products/{BookId}");

        Assert.Equal(("MISS", 7), (missed, before.GetProperty("available").GetInt32()));
        Assert.Equal(("HIT", 6), (hit, after.GetProperty("available").GetInt32()));
    }

    // Cache-aside: the cache never sees a write that doesn't clear it, so such a write shows only when the cached copy
    // expires. The TTL is that safety net; a write path clears what it changed
    [Fact]
    public async Task AWriteThatSkipsTheCacheStaysHiddenUntilTheCacheIsCleared()
    {
        await ReadAsync($"/api/v1/products/{BookId}");
        await RenameBookAsync("Renamed book");
        try
        {
            var (stale, cached) = await ReadAsync($"/api/v1/products/{BookId}");
            await Cache.ProductChangedAsync(BookId, fixture.BooksCategoryId);
            var (fresh, loaded) = await ReadAsync($"/api/v1/products/{BookId}");

            Assert.Equal(("HIT", "Second book"), (stale, cached.GetProperty("name").GetString()));
            Assert.Equal(("MISS", "Renamed book"), (fresh, loaded.GetProperty("name").GetString()));
        }
        finally
        {
            await RenameBookAsync("Second book");
            await Cache.ProductChangedAsync(BookId, fixture.BooksCategoryId);
        }
    }

    [Fact]
    public async Task OnlyEachListsFirstPageIsCached()
    {
        var books = $"/api/v1/products?category={fixture.BooksCategoryId}&pageSize=2";
        var (first, page) = await ReadAsync(books);
        var (again, _) = await ReadAsync(books);
        using var numbered = await fixture.Client.GetAsync($"{books}&page=2");
        using var keyset = await fixture.Client.GetAsync(
            $"{books}&cursor={Uri.EscapeDataString(page.GetProperty("nextCursor").GetString()!)}");

        Assert.Equal(("MISS", "HIT"), (first, again));
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (numbered.StatusCode, keyset.StatusCode));
        Assert.False(numbered.Headers.Contains(CacheReads.HeaderName));
        Assert.False(keyset.Headers.Contains(CacheReads.HeaderName));
    }

    [Fact]
    public async Task AProductsChangeClearsOnlyTheListsItIsIn()
    {
        var books = $"/api/v1/products?category={fixture.BooksCategoryId}";
        var home = $"/api/v1/products?category={ProductCatalogFixture.HomeCategoryId}";
        const string all = "/api/v1/products";
        foreach (var list in new[] { books, home, all })
        {
            await ReadAsync(list);
        }

        await Cache.ProductChangedAsync(BookId, fixture.BooksCategoryId);

        Assert.Equal(("MISS", "HIT", "MISS"),
            ((await ReadAsync(books)).Cache, (await ReadAsync(home)).Cache, (await ReadAsync(all)).Cache));
    }

    // Product details and lists show category names, so a change to the categories clears everything
    [Fact]
    public async Task ACategoryChangeClearsEverything()
    {
        string[] paths = ["/api/v1/categories", $"/api/v1/products/{BookId}", "/api/v1/products"];
        foreach (var path in paths)
        {
            await ReadAsync(path);
        }

        await Cache.CatalogChangedAsync();

        foreach (var path in paths)
        {
            Assert.Equal("MISS", (await ReadAsync(path)).Cache);
        }
    }

    // Stampede protection: while one request loads a key, the others asking for it wait for that load instead of all
    // going to the database. The load is held until every request has asked, so all of them miss together
    [Fact]
    public async Task ConcurrentMissesShareOneLoad()
    {
        var gate = new TaskCompletionSource<CachedProduct?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var reads = Enumerable.Range(0, 50)
            .Select(_ => Cache.ProductAsync(BookId, (_, _) =>
            {
                Interlocked.Increment(ref loads);
                return gate.Task;
            }, CancellationToken.None).AsTask())
            .ToList();

        gate.SetResult(null);
        var results = await Task.WhenAll(reads);

        Assert.Equal(1, loads);
        Assert.Equal(49, results.Count(read => read.Hit));
    }

    private async Task<(string Cache, JsonElement Body)> ReadAsync(string path)
    {
        using var response = await fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (Assert.Single(response.Headers.GetValues(CacheReads.HeaderName)),
            await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private async Task RenameBookAsync(string name)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ProductsDbContext>().Products.Where(product => product.Id == BookId)
            .ExecuteUpdateAsync(product => product.SetProperty(row => row.Name, name));
    }
}
