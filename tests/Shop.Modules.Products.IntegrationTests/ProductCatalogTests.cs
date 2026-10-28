using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Products.Catalog;
using Shop.Modules.Products.Contracts;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Paging;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

[Collection("Products database")]
public sealed class ProductCatalogTests(ProductCatalogFixture fixture) : IClassFixture<ProductCatalogFixture>
{
    [Fact]
    public async Task AnonymousListUsesDefaultPagingAndNewestFirstOrder()
    {
        var result = await ListAsync("/api/v1/products");

        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
        Assert.Equal(4, result.TotalCount);
        Assert.Equal(new[]
        {
            ProductCatalogFixture.HomeProductId, ProductCatalogFixture.NewestBookId,
            ProductCatalogFixture.FirstBookId, ProductCatalogFixture.SecondBookId,
        }, result.Items.Select(product => product.Id));
    }

    [Fact]
    public async Task CategoryFilterAndPagingDoNotRepeatOrSkipTiedProducts()
    {
        var path = $"/api/v1/products?category={fixture.BooksCategoryId}&pageSize=2";
        var first = await ListAsync(path + "&page=1");
        var second = await ListAsync(path + "&page=2");

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(3, second.TotalCount);
        Assert.Equal(2, first.PageSize);
        Assert.Equal(2, second.Page);
        Assert.Equal(new[]
        {
            ProductCatalogFixture.NewestBookId, ProductCatalogFixture.FirstBookId, ProductCatalogFixture.SecondBookId,
        }, first.Items.Concat(second.Items).Select(product => product.Id));
        Assert.All(first.Items.Concat(second.Items), product => Assert.Equal(fixture.BooksCategoryId, product.CategoryId));
    }

    [Fact]
    public async Task PageBeyondTheEndKeepsTheFilteredTotal()
    {
        var result = await ListAsync($"/api/v1/products?category={fixture.BooksCategoryId}&page=3&pageSize=2");

        Assert.Empty(result.Items);
        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task UnknownCategoryReturnsAnEmptyPage()
    {
        var result = await ListAsync($"/api/v1/products?category={Guid.CreateVersion7()}");

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task MaximumPageSizeIsAccepted()
    {
        var result = await ListAsync("/api/v1/products?pageSize=50");

        Assert.Equal(50, result.PageSize);
        Assert.Equal(4, result.Items.Count);
    }

    [Fact]
    public async Task AnonymousDetailsReturnAnImageUrlAndCategoryWithoutStoragePaths()
    {
        using var response = await fixture.Client.GetAsync($"/api/v1/products/{ProductCatalogFixture.SecondBookId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var product = (await response.Content.ReadFromJsonAsync<ProductResponse>())!;

        Assert.Equal("Second book", product.Name);
        Assert.Equal(19.95m, product.Price);
        Assert.Equal("/images/products/second%20book.png", product.ImageUrl);
        Assert.Equal("Books", product.CategoryName);
        Assert.Equal(TimeSpan.Zero, product.CreatedAt.Offset);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.TryGetProperty("imagePath", out _));
    }

    [Fact]
    public async Task MissingProductReturnsTheProducts404Contract()
    {
        using var problem = await AssertProblemAsync($"/api/v1/products/{Guid.CreateVersion7()}", HttpStatusCode.NotFound, ErrorCodes.Products.NotFound);
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?page=-1", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=-1", "pageSize")]
    [InlineData("?pageSize=51", "pageSize")]
    [InlineData("?category=00000000-0000-0000-0000-000000000000", "category")]
    [InlineData("?page=2147483647&pageSize=50", "page")]
    public async Task InvalidQueriesReturn422WithFieldErrors(string query, string field)
    {
        using var problem = await AssertProblemAsync("/api/v1/products" + query, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);

        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData("/api/v1/products?page=abc")]
    [InlineData("/api/v1/products?pageSize=abc")]
    [InlineData("/api/v1/products?page=2147483648")]
    [InlineData("/api/v1/products?category=abc")]
    [InlineData("/api/v1/products/not-a-uuid")]
    public async Task UnparseableParametersReturn400(string path)
    {
        using var problem = await AssertProblemAsync(path, HttpStatusCode.BadRequest, ErrorCodes.Common.BadRequest);
    }

    [Fact]
    public async Task AnonymousCategoriesAreSortedByName()
    {
        var categories = await fixture.Client.GetFromJsonAsync<List<CategoryResponse>>("/api/v1/categories");

        Assert.NotNull(categories);
        Assert.Equal(new[] { "Books", "Clothing", "Electronics", "Home" }, categories.Select(category => category.Name));
    }

    [Fact]
    public async Task OrderCatalogReturnsDatabaseNamesAndPricesOnceAndOmitsUnknownIds()
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalog>();
        var products = await catalog.GetByIdsAsync(new[]
        {
            ProductCatalogFixture.FirstBookId, ProductCatalogFixture.SecondBookId,
            ProductCatalogFixture.FirstBookId, Guid.CreateVersion7(),
        }, CancellationToken.None);

        Assert.Equal(2, products.Count);
        Assert.Equal(new ProductCatalogItem(ProductCatalogFixture.FirstBookId, "First book", 10m), products[ProductCatalogFixture.FirstBookId]);
        Assert.Equal(new ProductCatalogItem(ProductCatalogFixture.SecondBookId, "Second book", 19.95m), products[ProductCatalogFixture.SecondBookId]);
        Assert.Empty(await catalog.GetByIdsAsync(Array.Empty<Guid>(), CancellationToken.None));
    }

    [Fact]
    public async Task OrderCatalogHonorsCancellationEvenForAnEmptyRequest()
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalog>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.GetByIdsAsync(Array.Empty<Guid>(), new CancellationToken(canceled: true)));
    }

    private async Task<PagedResponse<ProductResponse>> ListAsync(string path) =>
        (await fixture.Client.GetFromJsonAsync<PagedResponse<ProductResponse>>(path))!;

    private async Task<JsonDocument> AssertProblemAsync(string path, HttpStatusCode status, string code)
    {
        using var response = await fixture.Client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
        return document;
    }
}
