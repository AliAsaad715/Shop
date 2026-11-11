using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Products.Catalog;
using Shop.Modules.Products.Contracts;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Domain;
using Shop.SharedKernel.Caching;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

public sealed class UpdateProductTests(CatalogCacheFixture fixture) : IClassFixture<CatalogCacheFixture>, IAsyncLifetime
{
    private static Guid Id => CatalogCacheFixture.ProductId(7);
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized, ErrorCodes.Common.Unauthorized)]
    [InlineData("Customer", HttpStatusCode.Forbidden, ErrorCodes.Common.Forbidden)]
    public async Task OnlyAdminsCanUpdateProducts(string? role, HttpStatusCode status, string code)
    {
        var before = await ReadAsync();
        using var response = await PutAsync(role: role);
        await ProblemAsync(response, status, code);
        Assert.Equal(before, await ReadAsync());
        Assert.Empty(fixture.Inventory.Lookups);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdminUpdatesAllFieldsButKeepsIdentityImageAndCreationTime(bool cacheEnabled)
    {
        if (!cacheEnabled) await fixture.ResetAsync(new() { ["CatalogCache:Enabled"] = "false" });
        var before = await ReadAsync();
        var body = Body();
        body["name"] = "  Updated product  ";
        body["description"] = "  Updated description  ";
        body["categoryId"] = fixture.OtherCategory;
        using var response = await PutAsync(body: body);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Empty(fixture.Inventory.Lookups);
        var after = await ReadAsync();
        Assert.Equal("Updated product", after.Name);
        Assert.Equal("Updated description", after.Description);
        Assert.Equal(29.95m, after.Price);
        Assert.Equal(fixture.OtherCategory, after.CategoryId);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.ImagePath, after.ImagePath);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.NotEqual(before.Version, after.Version);
    }

    public static TheoryData<string, object?> InvalidFields => new()
    {
        { "name", null }, { "name", "" }, { "name", "   " }, { "name", new string('n', 201) },
        { "description", null }, { "description", "" }, { "description", "   " }, { "description", new string('d', 2001) },
        { "price", null }, { "price", -0.01m }, { "price", 1.001m }, { "price", 10000000000000000m },
        { "categoryId", null }, { "categoryId", Guid.Empty },
    };

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public async Task InvalidFieldsReturn422WithoutChangingTheProduct(string field, object? value)
    {
        var before = await ReadAsync();
        var body = Body();
        body[field] = value;
        using var response = await PutAsync(body: body);
        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(before, await ReadAsync());
    }

    [Theory]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("price")]
    [InlineData("categoryId")]
    public async Task EveryFieldIsRequired(string field)
    {
        var body = Body();
        body.Remove(field);
        using var response = await PutAsync(body: body);
        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9999999999999999.99")]
    public async Task PriceAndStringLengthBoundariesAreAccepted(string price)
    {
        var body = Body();
        body["name"] = new string('n', 200);
        body["description"] = new string('d', 2000);
        body["price"] = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);
        using var response = await PutAsync(body: body);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var saved = await ReadAsync();
        Assert.Equal(body["price"], saved.Price);
        Assert.Equal(200, saved.Name.Length);
        Assert.Equal(2000, saved.Description.Length);
    }

    [Theory]
    [InlineData("{", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{\"price\":\"invalid\"}", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{\"categoryId\":\"invalid\"}", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{}", "text/plain", HttpStatusCode.UnsupportedMediaType)]
    public async Task BindingErrorsPreserveProblemDetails(string body, string mediaType, HttpStatusCode status)
    {
        var before = await ReadAsync();
        using var response = await PutAsync(content: new StringContent(body, Encoding.UTF8, mediaType));
        await ProblemAsync(response, status, status == HttpStatusCode.BadRequest
            ? ErrorCodes.Common.BadRequest : ErrorCodes.Common.UnsupportedMediaType);
        Assert.Equal(before, await ReadAsync());
    }

    [Fact]
    public async Task MissingProductReturns404()
    {
        using var response = await PutAsync(id: Guid.CreateVersion7());
        await ProblemAsync(response, HttpStatusCode.NotFound, ErrorCodes.Products.NotFound);
    }

    [Fact]
    public async Task UnknownCategoryReturns422AndLeavesWarmEntriesIntact()
    {
        await WarmAsync();
        var before = await ReadAsync();
        var body = Body();
        body["categoryId"] = Guid.CreateVersion7();
        using var response = await PutAsync(body: body);
        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);
        Assert.True(problem.GetProperty("errors").TryGetProperty("categoryId", out _));
        Assert.Equal(before, await ReadAsync());
        await AssertStillWarmAsync();
    }

    [Fact]
    public async Task EditingRefreshesWarmDetailsListsETagsAndOrderPrices()
    {
        using var original = await fixture.Client.GetAsync($"/api/v1/products/{Id}");
        var originalTag = original.Headers.ETag!.Tag;
        await PageAsync("/api/v1/products");
        await PageAsync($"/api/v1/products?category={fixture.Category}");
        using var updated = await PutAsync();
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/products/{Id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", originalTag);
        using var changed = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(originalTag, changed.Headers.ETag!.Tag);
        var product = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updated product", product.GetProperty("name").GetString());
        Assert.Equal(29.95m, product.GetProperty("price").GetDecimal());
        foreach (var path in new[] { "/api/v1/products", $"/api/v1/products?category={fixture.Category}" })
        {
            var page = await PageAsync(path);
            Assert.Contains(page.Items, p => p.Id == Id && p.Name == "Updated product" && p.Price == 29.95m);
        }
        using var revalidation = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/products/{Id}");
        revalidation.Headers.TryAddWithoutValidation("If-None-Match", changed.Headers.ETag.Tag);
        using var current = await fixture.Client.SendAsync(revalidation);
        Assert.Equal(HttpStatusCode.NotModified, current.StatusCode);
        Assert.Equal("0", Queries(current));
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var prices = await scope.ServiceProvider.GetRequiredService<IProductCatalog>().GetByIdsAsync([Id], CancellationToken.None);
        Assert.Equal(29.95m, prices[Id].Price);
    }

    [Fact]
    public async Task MovingInvalidatesAllCachedPagesAndSizesInBothCategoriesAndTheGeneralList()
    {
        var pages = (from category in new Guid?[] { null, fixture.Category, fixture.OtherCategory }
                     from size in new[] { 2, 3 }
                     from page in Enumerable.Range(1, 3)
                     select (Category: category, Size: size, Page: page,
                         Path: $"/api/v1/products?page={page}&pageSize={size}" + (category is null ? "" : $"&category={category}"))).ToArray();
        foreach (var page in pages) await PageAsync(page.Path);
        using var warm = await fixture.Client.GetAsync($"/api/v1/products/{Id}");
        var body = Body();
        body["categoryId"] = fixture.OtherCategory;
        using var response = await PutAsync(body: body);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        foreach (var page in pages)
        {
            var actual = await PageAsync(page.Path);
            Product[] expected = [];
            await fixture.UpdateAsync(async db => expected = await db.Products.AsNoTracking()
                .Where(p => page.Category == null || p.CategoryId == page.Category)
                .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).ToArrayAsync());
            Assert.Equal(expected.Length, actual.TotalCount);
            Assert.Equal(expected.Skip((page.Page - 1) * page.Size).Take(page.Size).Select(p => p.Id), actual.Items.Select(p => p.Id));
            Assert.All(actual.Items.Where(p => p.Id == Id), p =>
            {
                Assert.Equal("Updated product", p.Name);
                Assert.Equal(fixture.OtherCategory, p.CategoryId);
            });
        }
        using var details = await fixture.Client.GetAsync($"/api/v1/products/{Id}");
        Assert.Equal(fixture.OtherCategory, (await details.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task AnotherHostWithEmptyL1ReadsTheUpdatedSnapshotFromRedis()
    {
        await WarmAsync();
        await fixture.WaitForRedisAsync(CatalogCacheKeys.Product(Id));
        using var updated = await PutAsync();
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        using var fresh = await fixture.Client.GetAsync($"/api/v1/products/{Id}");
        var expected = await fresh.Content.ReadAsStringAsync();
        await fixture.WaitForRedisAsync(CatalogCacheKeys.Product(Id));
        using var other = await fixture.Second.GetAsync($"/api/v1/products/{Id}");
        Assert.Equal(expected, await other.Content.ReadAsStringAsync());
        Assert.Equal("0", Queries(other));
        Assert.Equal(0, fixture.SecondQueries.Count);
        foreach (var category in new Guid?[] { null, fixture.Category })
        {
            var path = "/api/v1/products" + (category is null ? "" : $"?category={category}");
            var rebuilt = await PageAsync(path);
            await fixture.WaitForRedisAsync(CatalogCacheKeys.List(new ListProductsRequest { Category = category }));
            using var page = await fixture.Second.GetAsync(path);
            var fromRedis = await page.Content.ReadFromJsonAsync<ProductPageResponse>();
            Assert.NotNull(fromRedis);
            Assert.Equal<ProductResponse>(rebuilt.Items, fromRedis.Items);
            Assert.Equal(rebuilt.TotalCount, fromRedis.TotalCount);
            Assert.Equal(rebuilt.NextCursor, fromRedis.NextCursor);
            Assert.Equal("0", Queries(page));
        }
    }

    [Fact]
    public async Task FailedSaveLeavesTheDatabaseAndWarmEntriesUnchanged()
    {
        await WarmAsync();
        var before = await ReadAsync();
        fixture.SaveFailure.FailNext = true;
        using var response = await PutAsync();
        await ProblemAsync(response, HttpStatusCode.InternalServerError, ErrorCodes.Common.InternalError);
        Assert.Equal(before, await ReadAsync());
        await AssertStillWarmAsync();
    }

    [Fact]
    public async Task ConcurrentEditReturns409WithoutOverwritingTheWinnerOrInvalidatingWarmEntries()
    {
        await WarmAsync();
        var gate = fixture.Saves.PauseBeforeSave();
        var pending = PutAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.UpdateAsync(db => db.Products.Where(p => p.Id == Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "Concurrent winner")));
        }
        finally { gate.Release.TrySetResult(); }
        using var response = await pending;
        await ProblemAsync(response, HttpStatusCode.Conflict, ErrorCodes.Common.ConcurrencyConflict);
        var saved = await ReadAsync();
        Assert.Equal("Concurrent winner", saved.Name);
        Assert.Equal(7m, saved.Price);
        await AssertStillWarmAsync();
    }

    [Fact]
    public async Task CategoryDeletedAfterValidationReturns422WithoutSavingOrInvalidating()
    {
        var categoryId = Guid.CreateVersion7();
        await fixture.UpdateAsync(async db =>
        {
            db.Categories.Add(new Category { Id = categoryId, Name = "Temporary update category" });
            await db.SaveChangesAsync();
        });
        await WarmAsync();
        var before = await ReadAsync();
        var body = Body();
        body["categoryId"] = categoryId;
        var gate = fixture.Saves.PauseBeforeSave();
        var pending = PutAsync(body: body);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.UpdateAsync(db => db.Categories.Where(c => c.Id == categoryId).ExecuteDeleteAsync());
        }
        finally { gate.Release.TrySetResult(); }
        using var response = await pending;
        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);
        Assert.True(problem.GetProperty("errors").TryGetProperty("categoryId", out _));
        Assert.Equal(before, await ReadAsync());
        await AssertStillWarmAsync();
    }

    [Fact]
    public async Task DisconnectAfterCommitDoesNotCancelInvalidation()
    {
        await WarmAsync();
        var key = CacheRegistration.RedisPrefix + CatalogCacheKeys.Product(Id);
        await fixture.WaitForRedisAsync(CatalogCacheKeys.Product(Id));
        using var disconnect = new CancellationTokenSource();
        var gate = fixture.Saves.PauseAfterCommit();
        var pending = PutAsync(ct: disconnect.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            disconnect.Cancel();
        }
        finally { gate.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await pending; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await fixture.Redis.KeyExistsAsync(key)) await Task.Delay(20, timeout.Token);
        using var fresh = await fixture.Client.GetAsync($"/api/v1/products/{Id}");
        Assert.Equal("Updated product", (await fresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.Contains((await PageAsync("/api/v1/products")).Items, p => p.Id == Id && p.Name == "Updated product");
    }

    [Fact]
    public async Task RedisOutageDoesNotFailAnAlreadyCommittedUpdate()
    {
        await fixture.ResetAsync(new()
        {
            ["ConnectionStrings:Redis"] = "127.0.0.1:1", ["CatalogCache:LocalTtl"] = "00:00:00.100",
        });
        await WarmAsync();
        using var response = await PutAsync();
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Updated product", (await ReadAsync()).Name);
        await Task.Delay(150);
        using var fresh = await fixture.Client.GetAsync($"/api/v1/products/{Id}");
        Assert.Equal("Updated product", (await fresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
    }

    private Dictionary<string, object?> Body() => new()
    {
        ["name"] = "Updated product", ["description"] = "Updated description", ["price"] = 29.95m, ["categoryId"] = fixture.Category,
    };

    private async Task<HttpResponseMessage> PutAsync(Guid? id = null, object? body = null, string? role = "Admin",
        HttpContent? content = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/products/{id ?? Id}")
            { Content = content ?? JsonContent.Create(body ?? Body()) };
        if (role is not null) request.Headers.Add("X-Test-Role", role);
        return await fixture.Client.SendAsync(request, ct);
    }

    private async Task<ProductSnapshot> ReadAsync()
    {
        ProductSnapshot? saved = null;
        await fixture.UpdateAsync(async db => saved = await db.Products.AsNoTracking().Where(p => p.Id == Id)
            .Select(p => new ProductSnapshot(p.Id, p.Name, p.Description, p.Price, p.CategoryId, p.ImagePath, p.CreatedAt, p.Version)).SingleAsync());
        return saved!;
    }

    private async Task WarmAsync()
    {
        using var product = await fixture.Client.GetAsync($"/api/v1/products/{Id}");
        product.EnsureSuccessStatusCode();
        await PageAsync("/api/v1/products");
        await PageAsync($"/api/v1/products?category={fixture.Category}");
    }

    private async Task AssertStillWarmAsync()
    {
        foreach (var path in new[] { $"/api/v1/products/{Id}", "/api/v1/products", $"/api/v1/products?category={fixture.Category}" })
        {
            using var response = await fixture.Client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            Assert.Equal("0", Queries(response));
        }
    }

    private async Task<ProductPageResponse> PageAsync(string path) =>
        (await fixture.Client.GetFromJsonAsync<ProductPageResponse>(path))!;

    private static string Queries(HttpResponseMessage response) => response.Headers.GetValues("X-Db-Queries").Single();

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        return problem;
    }

    private sealed record ProductSnapshot(Guid Id, string Name, string Description, decimal Price, Guid CategoryId,
        string ImagePath, DateTimeOffset CreatedAt, uint Version);
}
