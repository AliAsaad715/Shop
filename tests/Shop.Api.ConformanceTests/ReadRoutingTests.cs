using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Caching;
using Shop.SharedKernel.Persistence;
using Xunit;

namespace Shop.Api.ConformanceTests;

public sealed class ReadRoutingTests(ReadRoutingFixture fixture) : IClassFixture<ReadRoutingFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("/api/v1/products?page=4")]
    [InlineData("/api/v1/products?cursor=CURSOR")]
    public async Task UncachedCatalogReadsReallyUseTheLaggingReplica(string path)
    {
        if (path.Contains("CURSOR"))
        {
            using var first = await fixture.First.GetAsync("/api/v1/products");
            var cursor = (await BodyAsync(first)).GetProperty("nextCursor").GetString();
            path = path.Replace("CURSOR", Uri.EscapeDataString(cursor!));
        }
        await fixture.PauseAsync();
        await fixture.SqlAsync("UPDATE products.products SET price=77");
        using var stale = await fixture.First.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.All((await BodyAsync(stale)).GetProperty("items").EnumerateArray(), p => Assert.Equal(10m, p.GetProperty("price").GetDecimal()));
        await fixture.ResumeAsync(); await fixture.WaitForReplayAsync();
        using var caughtUp = await fixture.First.GetAsync(path);
        Assert.All((await BodyAsync(caughtUp)).GetProperty("items").EnumerateArray(), p => Assert.Equal(77m, p.GetProperty("price").GetDecimal()));
    }

    [Theory]
    [InlineData("/api/v1/products", "products:all:page:1:size:20", "items")]
    [InlineData("/api/v1/products/PRODUCT", "product:PRODUCT", "price")]
    [InlineData("/api/v1/categories", "categories", "categories")]
    public async Task EverySharedCacheFillUsesPrimaryEvenWhileReplicaIsPaused(string path, string key, string kind)
    {
        path = path.Replace("PRODUCT", fixture.Product.ToString());
        key = key.Replace("PRODUCT", fixture.Product.ToString());
        await fixture.PauseAsync();
        await fixture.SqlAsync($"UPDATE products.products SET price=77; UPDATE products.categories SET name='Fresh books' WHERE id='{fixture.Category}'");
        using var fresh = await fixture.First.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var body = await BodyAsync(fresh);
        if (kind == "items") Assert.All(body.GetProperty("items").EnumerateArray(), p => Assert.Equal(77m, p.GetProperty("price").GetDecimal()));
        else if (kind == "price") Assert.Equal(77m, body.GetProperty("price").GetDecimal());
        else Assert.Contains(body.EnumerateArray(), c => c.GetProperty("name").GetString() == "Fresh books");
        await ReadRoutingFixture.UntilAsync(async () => await fixture.Redis.KeyExistsAsync(CacheRegistration.RedisPrefix + key));
        using var other = await fixture.Second.GetAsync(path);
        Assert.Equal(await fresh.Content.ReadAsStringAsync(), await other.Content.ReadAsStringAsync());
        Assert.Equal("0", SqlCount(other));
    }

    [Theory]
    [InlineData("/api/v1/products")]
    [InlineData("/api/v1/products/PRODUCT")]
    [InlineData("/api/v1/categories")]
    public async Task DisablingCatalogCacheAllowsEligibleReadsToUseReplica(string path)
    {
        using var client = fixture.NewHost(new() { ["CatalogCache:Enabled"] = "false" });
        path = path.Replace("PRODUCT", fixture.Product.ToString());
        await fixture.PauseAsync();
        await fixture.SqlAsync($"UPDATE products.products SET price=77; UPDATE products.categories SET name='Fresh books' WHERE id='{fixture.Category}'");
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Fresh books", await response.Content.ReadAsStringAsync());
        var body = await BodyAsync(response);
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("items", out var items))
            Assert.All(items.EnumerateArray(), p => Assert.Equal(10m, p.GetProperty("price").GetDecimal()));
        else if (body.ValueKind == JsonValueKind.Object) Assert.Equal(10m, body.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task NewOrderIsVisibleToItsWriterOnBothHostsUntilMarkerExpires()
    {
        await fixture.PauseAsync();
        var id = await PlaceAsync();
        var key = CacheRegistration.RedisPrefix + DatabaseReadRouter.MarkerKey(ReadRoutingFixture.Customer);
        Assert.True(await fixture.Redis.KeyExistsAsync(key));
        foreach (var client in new[] { fixture.First, fixture.Second })
        {
            using var list = await fixture.SendAsync(client, HttpMethod.Get, "/api/v1/orders", ReadRoutingFixture.Customer);
            Assert.Contains((await BodyAsync(list)).GetProperty("items").EnumerateArray(), o => o.GetProperty("id").GetGuid() == id);
            using var details = await fixture.SendAsync(client, HttpMethod.Get, $"/api/v1/orders/{id}", ReadRoutingFixture.Customer);
            Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        }
        // An admin who didn't write is still reading the deliberately stale replica.
        using var staleAdmin = await fixture.SendAsync(fixture.Second, HttpMethod.Get, "/api/v1/orders", ReadRoutingFixture.Admin, Roles.Admin);
        Assert.Empty((await BodyAsync(staleAdmin)).GetProperty("items").EnumerateArray());
        await ReadRoutingFixture.UntilAsync(async () => !await fixture.Redis.KeyExistsAsync(key));
        await Task.Delay(50); // Let the writer's local fallback expire as well.
        using var stale = await fixture.SendAsync(fixture.First, HttpMethod.Get, "/api/v1/orders", ReadRoutingFixture.Customer);
        Assert.Empty((await BodyAsync(stale)).GetProperty("items").EnumerateArray());
        await fixture.ResumeAsync(); await fixture.WaitForReplayAsync();
        using var current = await fixture.SendAsync(fixture.Second, HttpMethod.Get, $"/api/v1/orders/{id}", ReadRoutingFixture.Customer);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var forbidden = await fixture.SendAsync(fixture.Second, HttpMethod.Get, $"/api/v1/orders/{id}", ReadRoutingFixture.OtherCustomer);
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [Fact]
    public async Task StatusChangePinsTheAdminAndKeepsOrderOwnershipChecks()
    {
        var id = await PlaceAsync();
        await fixture.WaitForReplayAsync(); await fixture.PauseAsync();
        using var updated = await fixture.SendAsync(fixture.First, HttpMethod.Patch, $"/api/v1/orders/{id}/status",
            ReadRoutingFixture.Admin, Roles.Admin, JsonContent.Create(new { status = "Confirmed" }));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var current = await fixture.SendAsync(fixture.Second, HttpMethod.Get, $"/api/v1/orders/{id}", ReadRoutingFixture.Admin, Roles.Admin);
        Assert.Equal("Confirmed", (await BodyAsync(current)).GetProperty("status").GetString());
        Assert.Equal("Pending", await ReadRoutingFixture.ScalarAsync(fixture.ReplicaConnection, $"SELECT status FROM orders.orders WHERE id='{id}'"));
        using var other = await fixture.SendAsync(fixture.Second, HttpMethod.Get, $"/api/v1/orders/{id}", ReadRoutingFixture.OtherCustomer);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task WriterBypassesAnotherHostsWarmL1AndGetsANewETag()
    {
        var path = $"/api/v1/products/{fixture.Product}";
        using var warm = await fixture.Second.GetAsync(path);
        var oldTag = warm.Headers.ETag!.Tag;
        await fixture.PauseAsync();
        using var updated = await UpdateAsync(fixture.First);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        using var fresh = await fixture.SendAsync(fixture.Second, HttpMethod.Get, path, ReadRoutingFixture.Admin, Roles.Admin);
        Assert.Equal(77m, (await BodyAsync(fresh)).GetProperty("price").GetDecimal());
        Assert.NotEqual(oldTag, fresh.Headers.ETag!.Tag);
        Assert.Equal("1", SqlCount(fresh));
        // Anonymous traffic on that same host still has its bounded old L1, proving the writer bypass mattered.
        using var anonymous = await fixture.Second.GetAsync(path);
        Assert.Equal(10m, (await BodyAsync(anonymous)).GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task OrderPlacementPricesComeFromPrimaryWhileReplicaStillHasTheOldPrice()
    {
        await fixture.PauseAsync();
        await fixture.SqlAsync("UPDATE products.products SET price=77");
        var id = await PlaceAsync();
        using var response = await fixture.SendAsync(fixture.First, HttpMethod.Get, $"/api/v1/orders/{id}", ReadRoutingFixture.Customer);
        Assert.Equal(77m, (await BodyAsync(response)).GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task NewUserProfileAndLoginStayOnPrimaryWhileReplicaHasNoUser()
    {
        await fixture.PauseAsync();
        var email = $"routing-{Guid.NewGuid():N}@shop.test";
        using var registered = await fixture.First.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Customer123!" });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var id = (await BodyAsync(registered)).GetProperty("id").GetGuid();
        using var login = await fixture.First.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Customer123!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var me = await fixture.SendAsync(fixture.Second, HttpMethod.Get, "/api/v1/users/me", id);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(email, (await BodyAsync(me)).GetProperty("email").GetString());
        Assert.Equal("0", await ReadRoutingFixture.ScalarAsync(fixture.ReplicaConnection, $"SELECT count(*)::text FROM users.users WHERE id='{id}'"));
    }

    [Fact]
    public async Task FailedWriteDoesNotCreateAReadYourWritesMarker()
    {
        using var response = await fixture.SendAsync(fixture.First, HttpMethod.Put, $"/api/v1/products/{Guid.NewGuid()}",
            ReadRoutingFixture.Admin, Roles.Admin, UpdateBody());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await fixture.Redis.KeyExistsAsync(CacheRegistration.RedisPrefix + DatabaseReadRouter.MarkerKey(ReadRoutingFixture.Admin)));
        await fixture.PauseAsync(); await fixture.SqlAsync("UPDATE products.products SET price=77");
        using var read = await fixture.SendAsync(fixture.First, HttpMethod.Get, "/api/v1/products?page=4", ReadRoutingFixture.Admin, Roles.Admin);
        Assert.All((await BodyAsync(read)).GetProperty("items").EnumerateArray(), p => Assert.Equal(10m, p.GetProperty("price").GetDecimal()));
    }

    [Fact]
    public async Task ProductCreationPinsItsAdminAcrossHostsAndBypassesWarmLists()
    {
        using var warm = await fixture.Second.GetAsync("/api/v1/products");
        await fixture.PauseAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("New routed product"), "name");
        form.Add(new StringContent("Created while replay is paused"), "description");
        form.Add(new StringContent("19.95"), "price");
        form.Add(new StringContent(fixture.Category.ToString()), "categoryId");
        form.Add(new ByteArrayContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGPQSFkFAAHuATczvmbSAAAAAElFTkSuQmCC")), "image", "test.png");
        using var created = await fixture.SendAsync(fixture.First, HttpMethod.Post, "/api/v1/products", ReadRoutingFixture.Admin, Roles.Admin, form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await BodyAsync(created)).GetProperty("id").GetGuid();
        Assert.True(await fixture.Redis.KeyExistsAsync(CacheRegistration.RedisPrefix + DatabaseReadRouter.MarkerKey(ReadRoutingFixture.Admin)));
        using var list = await fixture.SendAsync(fixture.Second, HttpMethod.Get, "/api/v1/products", ReadRoutingFixture.Admin, Roles.Admin);
        Assert.Contains((await BodyAsync(list)).GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == id);
        Assert.Equal("0", await ReadRoutingFixture.ScalarAsync(fixture.ReplicaConnection, $"SELECT count(*)::text FROM products.products WHERE id='{id}'"));
    }

    [Fact]
    public async Task FailedDatabaseSaveDoesNotPinTheWriter()
    {
        await fixture.SqlAsync("ALTER TABLE products.products ADD CONSTRAINT routing_reject_edit CHECK (name <> 'Updated product')");
        try
        {
            using var response = await UpdateAsync(fixture.First);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.False(await fixture.Redis.KeyExistsAsync(CacheRegistration.RedisPrefix + DatabaseReadRouter.MarkerKey(ReadRoutingFixture.Admin)));
            Assert.Equal("10.00", await ReadRoutingFixture.ScalarAsync(fixture.PrimaryConnection, $"SELECT price::text FROM products.products WHERE id='{fixture.Product}'"));
        }
        finally { await fixture.SqlAsync("ALTER TABLE products.products DROP CONSTRAINT routing_reject_edit"); }
    }

    [Fact]
    public async Task UnavailableReplicaFallsBackToPrimaryIncludingAnExistingPooledConnection()
    {
        using var initial = await fixture.First.GetAsync("/api/v1/products?page=4");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        await fixture.StopReplicaAsync();
        await fixture.SqlAsync("UPDATE products.products SET price=77");
        using var fallback = await fixture.First.GetAsync("/api/v1/products?page=4");
        Assert.True(fallback.StatusCode == HttpStatusCode.OK, await fallback.Content.ReadAsStringAsync());
        Assert.All((await BodyAsync(fallback)).GetProperty("items").EnumerateArray(), p => Assert.Equal(77m, p.GetProperty("price").GetDecimal()));
    }

    [Fact]
    public async Task RedisOutageReadsPrimaryAndDoesNotFailCommittedWrites()
    {
        using var client = fixture.NewHost(new()
        {
            ["ConnectionStrings:Redis"] = "127.0.0.1:1", ["CatalogCache:Enabled"] = "false",
        });
        await fixture.PauseAsync(); await fixture.SqlAsync("UPDATE products.products SET price=77");
        using var read = await fixture.SendAsync(client, HttpMethod.Get, "/api/v1/products?page=4", ReadRoutingFixture.Customer);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.All((await BodyAsync(read)).GetProperty("items").EnumerateArray(), p => Assert.Equal(77m, p.GetProperty("price").GetDecimal()));
        using var updated = await UpdateAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        using var fresh = await fixture.SendAsync(client, HttpMethod.Get, $"/api/v1/products/{fixture.Product}", ReadRoutingFixture.Admin, Roles.Admin);
        Assert.Equal(77m, (await BodyAsync(fresh)).GetProperty("price").GetDecimal());
    }

    [Theory]
    [InlineData("ReadRouting:PrimaryWindow", "00:00:00")]
    [InlineData("ConnectionStrings:ShopRead", "")]
    [InlineData("ConnectionStrings:Redis", "")]
    public void InvalidRoutingConfigurationFailsAtStartup(string key, string value) =>
        Assert.Throws<OptionsValidationException>(() => fixture.NewHost(new() { [key] = value, ["CatalogCache:Enabled"] = "false" }));

    private async Task<Guid> PlaceAsync()
    {
        using var response = await fixture.SendAsync(fixture.First, HttpMethod.Post, "/api/v1/orders", ReadRoutingFixture.Customer,
            body: JsonContent.Create(new { lines = new[] { new { productId = fixture.Product, quantity = 1 } } }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await BodyAsync(response)).GetProperty("id").GetGuid();
    }

    private JsonContent UpdateBody() => JsonContent.Create(new
        { name = "Updated product", description = "Updated description", price = 77m, categoryId = fixture.Category });
    private Task<HttpResponseMessage> UpdateAsync(HttpClient client) => fixture.SendAsync(client, HttpMethod.Put,
        $"/api/v1/products/{fixture.Product}", ReadRoutingFixture.Admin, Roles.Admin, UpdateBody());
    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();
    private static string SqlCount(HttpResponseMessage response) => response.Headers.GetValues("X-Db-Queries").Single();
}
