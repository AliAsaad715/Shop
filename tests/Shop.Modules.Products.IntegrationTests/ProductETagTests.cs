using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Products.Contracts;
using Shop.Modules.Products.Data;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

// Task R3-09: product details carry an ETag built from the row versions and the stock. Sent back in If-None-Match, it
// costs a 304 with no body until something in the body changes
[Collection("Products database")]
public sealed class ProductETagTests(ProductCatalogFixture fixture) : IClassFixture<ProductCatalogFixture>
{
    private static readonly Guid ProductId = ProductCatalogFixture.HomeProductId;

    [Fact]
    public async Task DetailsCarryTheirETagAndAskToBeCheckedEveryTime()
    {
        fixture.Inventory.Stock[ProductId] = 17;

        using var response = await GetAsync(ProductId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.Matches("^\"v[0-9]+-c[0-9]+-s17\"$", response.Headers.ETag.Tag);
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.False(response.Headers.CacheControl.NoStore);
    }

    [Fact]
    public async Task TheCurrentETagBackIsA304WithNoBody()
    {
        var etag = await CurrentETagAsync();

        using var response = await GetAsync(ProductId, etag);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(etag, response.Headers.ETag?.Tag);
        Assert.True(response.Headers.CacheControl!.NoCache);
    }

    [Theory]
    [InlineData("W/{0}")]          // weakened by a proxy that compressed the response
    [InlineData("\"other\", {0}")] // one of several copies the client holds
    [InlineData("*")]              // any copy at all
    public async Task IfNoneMatchComparesWeaklyAndTakesAListOrAnyCopy(string format)
    {
        var etag = await CurrentETagAsync();

        using var response = await GetAsync(ProductId, string.Format(format, etag));

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    [Fact]
    public async Task NewStockChangesTheETag()
    {
        fixture.Inventory.Stock[ProductId] = 17;
        var before = await CurrentETagAsync();
        fixture.Inventory.Stock[ProductId] = 16;

        using var response = await GetAsync(ProductId, before);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches("-s16\"$", response.Headers.ETag!.Tag);
        Assert.Equal(16, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("available").GetInt32());
    }

    [Fact]
    public async Task EditingTheProductChangesTheETag()
    {
        var before = await CurrentETagAsync();
        await UpdateAsync(context => context.Products.Where(product => product.Id == ProductId)
            .ExecuteUpdateAsync(product => product.SetProperty(row => row.Description, "Edited description.")));
        try
        {
            using var response = await GetAsync(ProductId, before);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEqual(before, response.Headers.ETag!.Tag);
            Assert.Equal("Edited description.",
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("description").GetString());
        }
        finally
        {
            await UpdateAsync(context => context.Products.Where(product => product.Id == ProductId)
                .ExecuteUpdateAsync(product => product.SetProperty(row => row.Description, "Catalog integration test product.")));
        }
    }

    // The details show the category's name, so renaming it is a change in the body as well
    [Fact]
    public async Task RenamingTheCategoryChangesTheETag()
    {
        var before = await CurrentETagAsync();
        await RenameHomeAsync("Home", "Home and garden");
        try
        {
            using var response = await GetAsync(ProductId, before);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEqual(before, response.Headers.ETag!.Tag);
            Assert.Equal("Home and garden",
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("categoryName").GetString());
        }
        finally
        {
            await RenameHomeAsync("Home and garden", "Home");
        }
    }

    // While the warehouse is down the details still load, with their own ETag, so a client's copy with a count is
    // replaced, and its copy without one is answered with 304s until the warehouse is back
    [Fact]
    public async Task UnknownStockHasItsOwnETag()
    {
        fixture.Inventory.Stock[ProductId] = 17;
        var known = await CurrentETagAsync();
        fixture.Inventory.Failure = () => new InventoryUnavailableException("The warehouse is down.");
        try
        {
            using var unknown = await GetAsync(ProductId, known);
            Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
            Assert.Matches("-s\\?\"$", unknown.Headers.ETag!.Tag);

            using var again = await GetAsync(ProductId, unknown.Headers.ETag.Tag);
            Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        }
        finally
        {
            fixture.Inventory.Failure = null;
        }
    }

    [Fact]
    public async Task AMissingProductHasNoETag()
    {
        using var response = await GetAsync(Guid.CreateVersion7(), "*");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.ETag);
    }

    private async Task<string> CurrentETagAsync()
    {
        using var response = await GetAsync(ProductId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private async Task<HttpResponseMessage> GetAsync(Guid productId, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/products/{productId}");
        if (ifNoneMatch is not null)
        {
            // Exactly as written, so each test controls the header's form
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await fixture.Client.SendAsync(request);
    }

    private Task RenameHomeAsync(string from, string to) =>
        UpdateAsync(context => context.Categories.Where(category => category.Name == from)
            .ExecuteUpdateAsync(category => category.SetProperty(row => row.Name, to)));

    private async Task UpdateAsync(Func<ProductsDbContext, Task> update)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        await update(scope.ServiceProvider.GetRequiredService<ProductsDbContext>());
    }
}
