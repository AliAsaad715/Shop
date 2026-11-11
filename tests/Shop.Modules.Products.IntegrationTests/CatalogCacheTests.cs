using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shop.Modules.Products.Catalog;
using Shop.Modules.Products.Contracts;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

public sealed class CatalogCacheTests(CatalogCacheFixture fixture) : IClassFixture<CatalogCacheFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("/api/v1/categories", 1)]
    [InlineData("/api/v1/products", 2)]
    [InlineData("/api/v1/products/019a2180-3000-7000-8000-000000000001", 1)]
    public async Task ColdReadsCountTheirSqlAndWarmReadsSendNone(string path, int cold)
    {
        await AssertQueriesAsync(fixture.Client, path, cold);
        await AssertQueriesAsync(fixture.Client, path, 0);
        Assert.Equal(cold, fixture.Queries.Count);
    }

    [Theory]
    [InlineData("/api/v1/categories", "categories")]
    [InlineData("/api/v1/products", "products:all:page:1:size:20")]
    [InlineData("/api/v1/products/019a2180-3000-7000-8000-000000000001", "product:019a2180-3000-7000-8000-000000000001")]
    public async Task AnotherHostDeserializesRedisWithoutReadingPostgres(string path, string key)
    {
        using var original = await fixture.Client.GetAsync(path);
        var expected = await original.Content.ReadAsStringAsync();
        await fixture.WaitForRedisAsync(key);
        using var other = await fixture.Second.GetAsync(path);
        Assert.Equal(expected, await other.Content.ReadAsStringAsync());
        Assert.Equal("0", Header(other));
        Assert.Equal(0, fixture.SecondQueries.Count);
    }

    [Fact]
    public async Task LocalExpiryUsesRedisThenDistributedExpiryRefills()
    {
        await fixture.ResetAsync(new() { ["CatalogCache:ProductTtl"] = "00:00:01", ["CatalogCache:LocalTtl"] = "00:00:00.100" });
        var id = CatalogCacheFixture.ProductId(1);
        var key = $"product:{id:D}";
        var path = $"/api/v1/products/{id}";
        await AssertQueriesAsync(fixture.Client, path, 1);
        await fixture.WaitForRedisAsync(key);
        var ttl = await fixture.Redis.KeyTimeToLiveAsync("shop:r3:v1:" + key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value.TotalSeconds, 0, 1);
        await Task.Delay(150);
        await AssertQueriesAsync(fixture.Client, path, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await fixture.Redis.KeyExistsAsync("shop:r3:v1:" + key)) await Task.Delay(20, timeout.Token);
        await Task.Delay(110);
        await AssertQueriesAsync(fixture.Client, path, 1);
    }

    [Fact]
    public async Task TwentyColdRequestsShareOneFill()
    {
        var gate = fixture.Queries.PauseNext();
        var requests = Enumerable.Range(0, 20).Select(_ => fixture.Client.GetAsync("/api/v1/categories")).ToArray();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.TrySetResult();
        foreach (var response in await Task.WhenAll(requests))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }
        Assert.Equal(1, fixture.Queries.Count);
    }

    [Fact]
    public async Task CancellingTheInitiatorAndDisposingItsScopeDoesNotCancelAnotherWaiter()
    {
        using var cancellation = new CancellationTokenSource();
        var owner = fixture.App.Services.CreateAsyncScope();
        await using var other = fixture.App.Services.CreateAsyncScope();
        var gate = fixture.Queries.PauseNext();
        var first = owner.ServiceProvider.GetRequiredService<CachedCatalogReader>().GetAsync(CatalogCacheFixture.ProductId(1), cancellation.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = other.ServiceProvider.GetRequiredService<CachedCatalogReader>().GetAsync(CatalogCacheFixture.ProductId(1), CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await owner.DisposeAsync();
        gate.Release.TrySetResult();
        Assert.Equal("Product 1", (await second.WaitAsync(TimeSpan.FromSeconds(5)))!.Name);
        Assert.Equal(1, fixture.Queries.Count);
    }

    [Fact]
    public async Task DefaultAndExplicitPagingShareTheKeyWhileFiltersAndSizesStaySeparate()
    {
        await AssertQueriesAsync(fixture.Client, "/api/v1/products", 2);
        await AssertQueriesAsync(fixture.Client, "/api/v1/products?page=1&pageSize=20", 0);
        var books = await PageAsync($"/api/v1/products?category={fixture.Category}&pageSize=2");
        var home = await PageAsync($"/api/v1/products?category={fixture.OtherCategory}&pageSize=2");
        var next = await PageAsync($"/api/v1/products?category={fixture.Category}&page=2&pageSize=2");
        var larger = await PageAsync($"/api/v1/products?category={fixture.Category}&pageSize=3");
        Assert.All(books.Items, p => Assert.Equal(fixture.Category, p.CategoryId));
        Assert.All(home.Items, p => Assert.Equal(fixture.OtherCategory, p.CategoryId));
        Assert.Empty(books.Items.Select(p => p.Id).Intersect(next.Items.Select(p => p.Id)));
        Assert.Equal(3, larger.Items.Count);
        await AssertQueriesAsync(fixture.Client, $"/api/v1/products?category={fixture.Category.ToString().ToUpperInvariant()}&pageSize=2&page=1", 0);
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(2, 0)] [InlineData(3, 0)] [InlineData(4, 2)]
    public async Task OnlyTheFirstThreeNumberedPagesAreCached(int page, int repeatedQueries)
    {
        var path = $"/api/v1/products?page={page}&pageSize=1";
        await AssertQueriesAsync(fixture.Client, path, 2);
        await AssertQueriesAsync(fixture.Client, path, repeatedQueries);
    }

    [Fact]
    public async Task CursorPagesAndInvalidRequestsNeverUseTheCache()
    {
        var page = await PageAsync("/api/v1/products?pageSize=1");
        var path = $"/api/v1/products?pageSize=1&cursor={Uri.EscapeDataString(page.NextCursor!)}";
        await AssertQueriesAsync(fixture.Client, path, 1);
        await AssertQueriesAsync(fixture.Client, path, 1);
        var before = fixture.Queries.Count;
        using var invalid = await fixture.Client.GetAsync("/api/v1/products?page=0");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(before, fixture.Queries.Count);
    }

    [Fact]
    public async Task WarmDetailsStillReadLiveStockAndReflectWarehouseFailure()
    {
        var id = CatalogCacheFixture.ProductId(1);
        fixture.Inventory.Stock[id] = 7;
        Assert.Equal(7, (await DetailsAsync(id)).GetProperty("available").GetInt32());
        fixture.Inventory.Stock[id] = 0;
        Assert.Equal("out_of_stock", (await DetailsAsync(id)).GetProperty("stockStatus").GetString());
        fixture.Inventory.Failure = () => new InventoryUnavailableException("Offline.");
        Assert.Equal("unknown", (await DetailsAsync(id)).GetProperty("stockStatus").GetString());
        Assert.Equal(1, fixture.Queries.Count);
        Assert.Equal(3, fixture.Inventory.Lookups.Count);
    }

    [Fact]
    public async Task OrderPricesBypassTheDisplayCache()
    {
        var id = CatalogCacheFixture.ProductId(1);
        await DetailsAsync(id);
        await fixture.UpdateAsync(db => db.Products.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 99m)));
        Assert.Equal(1m, (await DetailsAsync(id)).GetProperty("price").GetDecimal());
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var prices = await scope.ServiceProvider.GetRequiredService<IProductCatalog>().GetByIdsAsync([id], CancellationToken.None);
        Assert.Equal(99m, prices[id].Price);
    }

    [Fact]
    public async Task MovingAProductInvalidatesItsDetailsAndBothCategoriesAndTheGeneralList()
    {
        var id = CatalogCacheFixture.ProductId(1);
        var paths = new[] { "/api/v1/products", $"/api/v1/products?category={fixture.Category}", $"/api/v1/products?category={fixture.OtherCategory}" };
        foreach (var path in paths) await PageAsync(path);
        await DetailsAsync(id);
        await fixture.UpdateAsync(db => db.Products.Where(p => p.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.CategoryId, fixture.OtherCategory).SetProperty(p => p.Name, "Moved product")));
        await fixture.InvalidateProductAsync(id, fixture.Category, fixture.OtherCategory);
        Assert.Equal("Moved product", (await DetailsAsync(id)).GetProperty("name").GetString());
        Assert.Contains((await PageAsync(paths[0])).Items, p => p.Id == id && p.Name == "Moved product");
        Assert.DoesNotContain((await PageAsync(paths[1])).Items, p => p.Id == id);
        Assert.Contains((await PageAsync(paths[2])).Items, p => p.Id == id);
    }

    [Fact]
    public async Task CategoryRenameInvalidatesAllBodiesContainingItsName()
    {
        await AssertQueriesAsync(fixture.Client, "/api/v1/categories", 1);
        await DetailsAsync(CatalogCacheFixture.ProductId(1));
        await PageAsync("/api/v1/products");
        await fixture.UpdateAsync(db => db.Categories.Where(c => c.Id == fixture.Category).ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, "Renamed books")));
        await using var scope = fixture.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CatalogCacheInvalidator>().CategoriesChangedAsync();
        var categories = await fixture.Client.GetFromJsonAsync<List<CategoryResponse>>("/api/v1/categories");
        Assert.Contains(categories!, c => c.Id == fixture.Category && c.Name == "Renamed books");
        Assert.Equal("Renamed books", (await DetailsAsync(CatalogCacheFixture.ProductId(1))).GetProperty("categoryName").GetString());
        Assert.Contains((await PageAsync("/api/v1/products")).Items, p => p.CategoryName == "Renamed books");
    }

    [Fact]
    public async Task CreationInvalidatesListsOnlyAfterTheSaveSucceeds()
    {
        var path = $"/api/v1/products?category={fixture.Category}";
        var before = await PageAsync(path);
        await PageAsync("/api/v1/products");
        fixture.SaveFailure.FailNext = true;
        using (var failure = await fixture.CreateAsync()) Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        await AssertQueriesAsync(fixture.Client, path, 0);
        using (var created = await fixture.CreateAsync()) Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var after = await PageAsync(path);
        Assert.Equal(before.TotalCount + 1, after.TotalCount);
        Assert.Equal("Created with cache", after.Items[0].Name);
        Assert.Equal("Created with cache", (await PageAsync("/api/v1/products")).Items[0].Name);
    }

    [Fact]
    public async Task UnknownProductsAreNeverCachedAndDatabaseFailuresStayFailures()
    {
        var path = $"/api/v1/products/{Guid.CreateVersion7()}";
        for (var i = 0; i < 2; i++)
        {
            using var response = await fixture.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("1", Header(response));
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("products.not_found", problem.GetProperty("code").GetString());
        }
        Assert.Empty(fixture.Inventory.Lookups);
        fixture.Queries.FailNext = true;
        using var failed = await fixture.Client.GetAsync($"/api/v1/products/{CatalogCacheFixture.ProductId(1)}");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await AssertQueriesAsync(fixture.Client, $"/api/v1/products/{CatalogCacheFixture.ProductId(1)}", 1);
    }

    [Fact]
    public async Task RedisOutageStillServesColdReadsAndCommittedWrites()
    {
        await fixture.ResetAsync(new()
        {
            ["ConnectionStrings:Redis"] = "127.0.0.1:1",
            ["CatalogCache:LocalTtl"] = "00:00:00.100",
        });
        await AssertQueriesAsync(fixture.Client, "/api/v1/categories", 1);
        await PageAsync("/api/v1/products");
        using var created = await fixture.CreateAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        // Failed tag invalidation may leave L1 until its expiry; the committed write still succeeds.
        await fixture.UpdateAsync(async db => Assert.True(await db.Products.AnyAsync(p => p.Name == "Created with cache")));
        await Task.Delay(150);
        Assert.Equal("Created with cache", (await PageAsync("/api/v1/products")).Items[0].Name);
    }

    [Fact]
    public async Task DemoSeederInvalidatesAlreadyWarmListsWhenItAddsProducts()
    {
        var before = await PageAsync("/api/v1/products");
        await fixture.SeedDemoAsync();
        var after = await PageAsync("/api/v1/products");
        Assert.Equal(before.TotalCount + 6, after.TotalCount);
    }

    [Theory]
    [InlineData("CatalogCache:ProductTtl", "00:00:00")]
    [InlineData("CatalogCache:LocalTtl", "00:11:00")]
    [InlineData("CatalogCache:MaxCachedPage", "4")]
    [InlineData("ConnectionStrings:Redis", "")]
    public async Task InvalidCacheConfigurationFailsAtStartup(string setting, string value)
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => fixture.ResetAsync(new() { [setting] = value }));
    }

    private async Task<ProductPageResponse> PageAsync(string path) => (await fixture.Client.GetFromJsonAsync<ProductPageResponse>(path))!;
    private async Task<JsonElement> DetailsAsync(Guid id) => await fixture.Client.GetFromJsonAsync<JsonElement>($"/api/v1/products/{id}");
    private static string Header(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues("X-Db-Queries"));
    private static async Task AssertQueriesAsync(HttpClient client, string path, int count)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(count.ToString(CultureInfo.InvariantCulture), Header(response));
    }
}
