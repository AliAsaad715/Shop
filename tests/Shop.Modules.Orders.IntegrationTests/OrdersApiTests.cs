using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shop.Modules.Orders.Data;
using Shop.Modules.Orders.Domain;
using Shop.Modules.Orders.Reading;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Modules.Orders.IntegrationTests;

[Collection("Orders database")]
public sealed class OrdersApiTests(OrdersApiFixture fixture)
{
    [Fact]
    public async Task CustomerPlacesAnOrderPricedByTheCatalog()
    {
        using var client = fixture.Customer(OrdersApiFixture.CustomerA);
        using var response = await client.PostAsJsonAsync("/api/v1/orders", new
        {
            lines = new[]
            {
                new { productId = FakeProductCatalog.Book.Id, quantity = 2 },
                new { productId = FakeProductCatalog.Lamp.Id, quantity = 1 },
            },
            // Ignored: the customer and the prices never come from the body
            customerId = OrdersApiFixture.CustomerB,
            total = 0.01m,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ReadAsync(response);
        Assert.Equal($"/api/v1/orders/{order.GetProperty("id").GetGuid()}", response.Headers.Location?.OriginalString);
        Assert.Equal(OrdersApiFixture.CustomerA, order.GetProperty("customerId").GetGuid());
        Assert.Equal("Pending", order.GetProperty("status").GetString());
        Assert.Equal(85.40m, order.GetProperty("total").GetDecimal());
        Assert.Equal(2, order.GetProperty("lines").GetArrayLength());
        Assert.Equal(1, order.GetProperty("history").GetArrayLength());

        var orderId = order.GetProperty("id").GetGuid();
        var reserved = Assert.Single(fixture.Inventory.Reserved, reservation => reservation.OrderId == orderId);
        Assert.Equal(3, reserved.Lines.Sum(line => line.Quantity));

        await using var scope = fixture.App.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Orders
            .Include(stored => stored.Lines).SingleAsync(stored => stored.Id == orderId);
        Assert.Equal(85.40m, saved.Total);
        Assert.Contains(saved.Lines, line => line is { ProductName: "Book", UnitPrice: 19.95m, Quantity: 2, LineTotal: 39.90m });
    }

    [Fact]
    public async Task UnknownProductIs422OnThatLine()
    {
        using var client = fixture.Customer(OrdersApiFixture.CustomerA);
        using var response = await client.PostAsJsonAsync("/api/v1/orders", new
        {
            lines = new[]
            {
                new { productId = FakeProductCatalog.Book.Id, quantity = 1 },
                new { productId = Guid.CreateVersion7(), quantity = 1 },
            },
        });

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);
        Assert.True(problem.GetProperty("errors").TryGetProperty("lines[1].productId", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(1001)]
    public async Task BadQuantityIs422(int quantity)
    {
        using var client = fixture.Customer(OrdersApiFixture.CustomerA);
        using var response = await client.PostAsJsonAsync("/api/v1/orders",
            new { lines = new[] { new { productId = FakeProductCatalog.Book.Id, quantity } } });

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);
        Assert.True(problem.GetProperty("errors").TryGetProperty("lines[0].quantity", out _));
    }

    [Fact]
    public async Task RefusedReservationIs409AndSavesNothing()
    {
        using var client = fixture.Customer(OrdersApiFixture.CustomerB);
        fixture.Inventory.RefuseReservations = true;
        try
        {
            using var response = await client.PostAsJsonAsync("/api/v1/orders",
                new { lines = new[] { new { productId = FakeProductCatalog.Lamp.Id, quantity = 999 } } });

            await AssertProblemAsync(response, HttpStatusCode.Conflict, ErrorCodes.Orders.InsufficientStock);
        }
        finally
        {
            fixture.Inventory.RefuseReservations = false;
        }

        await using var scope = fixture.App.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().OrderLines.AnyAsync(line => line.Quantity == 999));
    }

    [Fact]
    public async Task OnlyCustomersCanPlaceOrders()
    {
        using var anonymous = fixture.Anonymous();
        using var admin = fixture.AdminClient();
        var body = new { lines = new[] { new { productId = FakeProductCatalog.Book.Id, quantity = 1 } } };

        using var anonymousResponse = await anonymous.PostAsJsonAsync("/api/v1/orders", body);
        using var adminResponse = await admin.PostAsJsonAsync("/api/v1/orders", body);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, adminResponse.StatusCode);
    }

    [Fact]
    public async Task CustomerReadsTheirOwnOrder()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerB);
        using var client = fixture.Customer(OrdersApiFixture.CustomerB);

        using var response = await client.GetAsync($"/api/v1/orders/{orderId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(orderId, (await ReadAsync(response)).GetProperty("id").GetGuid());
    }

    // Required proof 1: a customer opening another customer's order is rejected, and the attempt is logged
    [Fact]
    public async Task SomeoneElsesOrderIs404AndLoggedAsADeniedAttempt()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerB);
        using var client = fixture.Customer(OrdersApiFixture.CustomerA);

        using var foreign = await client.GetAsync($"/api/v1/orders/{orderId}");
        using var missing = await client.GetAsync($"/api/v1/orders/{Guid.CreateVersion7()}");

        // Indistinguishable from an order that doesn't exist
        var foreignProblem = await AssertProblemAsync(foreign, HttpStatusCode.NotFound, ErrorCodes.Orders.NotFound);
        var missingProblem = await AssertProblemAsync(missing, HttpStatusCode.NotFound, ErrorCodes.Orders.NotFound);
        Assert.Equal(missingProblem.GetProperty("title").GetString(), foreignProblem.GetProperty("title").GetString());
        Assert.Contains(fixture.Logs.Entries, entry =>
            entry.Category == OrderAccess.SecurityLogCategory && entry.Level == LogLevel.Warning
            && entry.Message.Contains(orderId.ToString(), StringComparison.Ordinal)
            && entry.Message.Contains(OrdersApiFixture.CustomerA.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdminReadsAnyOrder()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerB);
        using var admin = fixture.AdminClient();

        using var response = await admin.GetAsync($"/api/v1/orders/{orderId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CustomerListSeesOnlyTheirOwnOrdersNewestFirst()
    {
        var customer = Guid.CreateVersion7();
        var first = await fixture.PlaceOrderAsync(customer);
        var second = await fixture.PlaceOrderAsync(customer, bookQuantity: 4);
        await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerB);
        using var client = fixture.Customer(customer);

        var page = await ReadAsync(await client.GetAsync("/api/v1/orders?pageSize=50"));

        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { second, first }, items.Select(item => item.GetProperty("id").GetGuid()));
        Assert.All(items, item => Assert.Equal(customer, item.GetProperty("customerId").GetGuid()));
        Assert.Equal(4, items[0].GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task AdminListSeesEveryCustomerAndFiltersByStatus()
    {
        var pending = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerA);
        var cancelled = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerB);
        using var admin = fixture.AdminClient();
        (await admin.PatchAsJsonAsync($"/api/v1/orders/{cancelled}/status", new { status = "Cancelled" })).EnsureSuccessStatusCode();

        var all = await ReadAsync(await admin.GetAsync("/api/v1/orders?pageSize=50"));
        var onlyCancelled = await ReadAsync(await admin.GetAsync("/api/v1/orders?status=Cancelled&pageSize=50"));

        var allIds = all.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(pending, allIds);
        Assert.Contains(cancelled, allIds);
        var cancelledItems = onlyCancelled.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(cancelled, cancelledItems.Select(item => item.GetProperty("id").GetGuid()));
        Assert.All(cancelledItems, item => Assert.Equal("Cancelled", item.GetProperty("status").GetString()));
    }

    [Theory]
    [InlineData("?pageSize=51")]
    [InlineData("?page=0")]
    [InlineData("?status=42")]
    public async Task BadListQueryIs422(string query)
    {
        using var client = fixture.Customer(OrdersApiFixture.CustomerA);

        using var response = await client.GetAsync("/api/v1/orders" + query);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);
    }

    [Fact]
    public async Task AnonymousReadsAre401()
    {
        using var client = fixture.Anonymous();

        using var list = await client.GetAsync("/api/v1/orders");
        using var single = await client.GetAsync($"/api/v1/orders/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, single.StatusCode);
    }

    // Required proof 2: a customer can't change an order's status, not even their own
    [Fact]
    public async Task CustomerChangingStatusIs403()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerA);
        using var client = fixture.Customer(OrdersApiFixture.CustomerA);

        using var response = await client.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status = "Confirmed" });

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Common.Forbidden);
        using var check = await client.GetAsync($"/api/v1/orders/{orderId}");
        Assert.Equal("Pending", (await ReadAsync(check)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AdminWalksAnOrderToDeliveredAndTheCustomerSeesIt()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerB);
        using var admin = fixture.AdminClient();

        foreach (var status in new[] { "Confirmed", "Shipped", "Delivered" })
        {
            using var response = await admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(status, (await ReadAsync(response)).GetProperty("status").GetString());
        }

        using var customer = fixture.Customer(OrdersApiFixture.CustomerB);
        var order = await ReadAsync(await customer.GetAsync($"/api/v1/orders/{orderId}"));
        Assert.Equal("Delivered", order.GetProperty("status").GetString());
        var history = order.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(new[] { "Pending", "Confirmed", "Shipped", "Delivered" }, history.Select(change => change.GetProperty("toStatus").GetString()));
        Assert.All(history.Skip(1), change => Assert.Equal(OrdersApiFixture.Admin, change.GetProperty("changedBy").GetGuid()));
        Assert.DoesNotContain(orderId, fixture.Inventory.Released);
    }

    [Theory]
    [InlineData("Delivered")]
    [InlineData("Shipped")]
    [InlineData("Pending")]
    public async Task InvalidTransitionIs409(string status)
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerA);
        using var admin = fixture.AdminClient();

        using var response = await admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status });

        var problem = await AssertProblemAsync(response, HttpStatusCode.Conflict, ErrorCodes.Orders.InvalidStatusTransition);
        Assert.Equal("https://shop.local/errors/invalid-status-transition", problem.GetProperty("type").GetString());
        Assert.Equal($"An order in status Pending can't move to {status}.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task CancellingReleasesTheReservationAndIsFinal()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerA);
        using var admin = fixture.AdminClient();

        using var cancelled = await admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status = "Cancelled" });
        using var reopened = await admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status = "Confirmed" });

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Contains(orderId, fixture.Inventory.Released);
        await AssertProblemAsync(reopened, HttpStatusCode.Conflict, ErrorCodes.Orders.InvalidStatusTransition);
    }

    [Fact]
    public async Task UnknownOrderStatusChangeIs404()
    {
        using var admin = fixture.AdminClient();

        using var response = await admin.PatchAsJsonAsync($"/api/v1/orders/{Guid.CreateVersion7()}/status", new { status = "Confirmed" });

        await AssertProblemAsync(response, HttpStatusCode.NotFound, ErrorCodes.Orders.NotFound);
    }

    [Fact]
    public async Task MissingStatusIs422AndUnknownStatusNameIs400()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerA);
        using var admin = fixture.AdminClient();

        using var missing = await admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { });
        using var unknown = await admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status = "Lost" });

        await AssertProblemAsync(missing, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);
        await AssertProblemAsync(unknown, HttpStatusCode.BadRequest, ErrorCodes.Common.BadRequest);
    }

    [Fact]
    public async Task ConcurrentEditLosesWith409()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerA);

        // Two admins load the same version; the second save must not overwrite the first
        await using var firstScope = fixture.App.Services.CreateAsyncScope();
        await using var secondScope = fixture.App.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var firstCopy = await first.Orders.Include(order => order.History).SingleAsync(order => order.Id == orderId);
        var secondCopy = await second.Orders.Include(order => order.History).SingleAsync(order => order.Id == orderId);

        firstCopy.MoveTo(OrderStatus.Confirmed, OrdersApiFixture.Admin, DateTimeOffset.UtcNow);
        await first.SaveChangesAsync();
        secondCopy.MoveTo(OrderStatus.Cancelled, OrdersApiFixture.Admin, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));

        return problem;
    }
}
