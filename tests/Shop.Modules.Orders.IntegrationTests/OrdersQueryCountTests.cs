using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Shop.SharedKernel.Diagnostics;
using Xunit;

namespace Shop.Modules.Orders.IntegrationTests;

// The Orders host in Development, where every response says how many SQL commands its request sent (X-Db-Queries)
public sealed class OrdersQueryCountFixture : OrdersApiFixture
{
    protected override string EnvironmentName => Environments.Development;
}

// Guards against N+1 (task R3-06): a request sends the same number of SQL commands however many lines, history rows
// or orders it touches. The catalog is a test double here, so these count the Orders module's own commands
[Collection("Orders database")]
public sealed class OrdersQueryCountTests(OrdersQueryCountFixture fixture) : IClassFixture<OrdersQueryCountFixture>
{
    [Fact]
    public async Task OrderDetailsIsOneQueryWhateverTheOrderHolds()
    {
        var customer = Guid.CreateVersion7();
        var small = await PlaceAsync(customer, FakeProductCatalog.Book);
        var large = await PlaceAsync(customer, [.. FakeProductCatalog.All]);

        // Through every status, so its history holds the most rows an order can have: 4
        using (var admin = fixture.AdminClient())
        {
            foreach (var status in new[] { "Confirmed", "Shipped", "Delivered" })
            {
                using var moved = await admin.PatchAsJsonAsync($"/api/v1/orders/{large}/status", new { status });
                Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
            }
        }

        using var client = fixture.Customer(customer);
        using var smallResponse = await client.GetAsync($"/api/v1/orders/{small}");
        using var largeResponse = await client.GetAsync($"/api/v1/orders/{large}");
        var largeOrder = await largeResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(5, largeOrder.GetProperty("lines").GetArrayLength());
        Assert.Equal(4, largeOrder.GetProperty("history").GetArrayLength());
        Assert.Equal("1", Count(smallResponse));
        Assert.Equal("1", Count(largeResponse));
    }

    [Fact]
    public async Task MyOrdersIsTwoQueriesWhateverThePageSize()
    {
        var customer = Guid.CreateVersion7();
        for (var order = 0; order < 3; order++)
        {
            await PlaceAsync(customer, FakeProductCatalog.Book);
        }

        using var client = fixture.Customer(customer);
        using var onePerPage = await client.GetAsync("/api/v1/orders?pageSize=1");
        using var allOnOnePage = await client.GetAsync("/api/v1/orders?pageSize=50");

        // The page, and its total
        Assert.Equal("2", Count(onePerPage));
        Assert.Equal("2", Count(allOnOnePage));
    }

    [Fact]
    public async Task PlacingAnOrderSavesInOneRoundTripWhateverItsLines()
    {
        using var client = fixture.Customer(Guid.CreateVersion7());
        using var oneLine = await PostAsync(client, FakeProductCatalog.Book);
        using var fiveLines = await PostAsync(client, [.. FakeProductCatalog.All]);

        // The order, its lines and its first history row go in one batch
        Assert.Equal("1", Count(oneLine));
        Assert.Equal("1", Count(fiveLines));
    }

    [Fact]
    public async Task ChangingAStatusIsOneReadAndOneSave()
    {
        var order = await PlaceAsync(Guid.CreateVersion7(), [.. FakeProductCatalog.All]);

        using var admin = fixture.AdminClient();
        using var response = await admin.PatchAsJsonAsync($"/api/v1/orders/{order}/status", new { status = "Confirmed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2", Count(response));
    }

    private async Task<Guid> PlaceAsync(Guid customerId, params Products.Contracts.ProductCatalogItem[] products)
    {
        using var client = fixture.Customer(customerId);
        using var response = await PostAsync(client, products);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, params Products.Contracts.ProductCatalogItem[] products) =>
        client.PostAsJsonAsync("/api/v1/orders",
            new { lines = products.Select(product => new { productId = product.Id, quantity = 1 }) });

    private static string Count(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues(DbQueryCountHeaderExtensions.HeaderName));
}
