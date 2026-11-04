using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Orders.Data;
using Shop.Modules.Orders.Releases;
using Shop.Modules.Products.Contracts;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Modules.Orders.IntegrationTests;

/// <summary>
/// What customers and admins get when the warehouse refuses, is down or is slow, and how a release the
/// warehouse can't take is queued and finished later. Every exit other than 201 leaves no stock reserved.
/// </summary>
[Collection("Orders database")]
public sealed class InventoryFailureTests(OrdersApiFixture fixture) : IDisposable
{
    private Guid LastReserveAttempt => fixture.Inventory.ReserveAttempts.Last();

    [Fact]
    public async Task ShortStockIs409ListingEachShortProduct()
    {
        fixture.Inventory.RefuseReservations = true;
        fixture.Inventory.Shortages = [new StockShortage(FakeProductCatalog.Lamp.Id, 3, 1)];

        using var response = await PlaceLampOrderAsync(quantity: 3);

        var problem = await AssertProblemAsync(response, HttpStatusCode.Conflict, ErrorCodes.Orders.InsufficientStock);
        var shortage = Assert.Single(problem.GetProperty("shortages").EnumerateArray());
        Assert.Equal(FakeProductCatalog.Lamp.Id, shortage.GetProperty("productId").GetGuid());
        Assert.Equal(3, shortage.GetProperty("requested").GetInt32());
        Assert.Equal(1, shortage.GetProperty("available").GetInt32());
        await AssertNotSavedAsync(LastReserveAttempt);

        // A refused reservation holds nothing, so nothing goes back
        Assert.DoesNotContain(LastReserveAttempt, fixture.Inventory.ReleaseAttempts);
    }

    [Fact]
    public async Task WarehouseDownIs503WithRetryAfterAndReleasesInCaseTheReserveLanded()
    {
        fixture.Inventory.ReserveFailure = () => new InventoryUnavailableException("The warehouse is down.");

        using var response = await PlaceLampOrderAsync();

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.Orders.WarehouseUnavailable);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        await AssertNotSavedAsync(LastReserveAttempt);
        Assert.Contains(LastReserveAttempt, fixture.Inventory.Released);
    }

    [Fact]
    public async Task SlowWarehouseIs504AndTheReservationIsReleased()
    {
        fixture.Inventory.ReserveFailure = () => new InventoryTimeoutException("The warehouse missed the deadline.");

        using var response = await PlaceLampOrderAsync();

        await AssertProblemAsync(response, HttpStatusCode.GatewayTimeout, ErrorCodes.Orders.WarehouseTimeout);
        await AssertNotSavedAsync(LastReserveAttempt);
        Assert.Contains(LastReserveAttempt, fixture.Inventory.Released);
    }

    [Fact]
    public async Task FailedSaveReleasesTheReservationAndIs500()
    {
        fixture.SaveFailure.FailNextOrderSave = true;

        using var response = await PlaceLampOrderAsync();

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, ErrorCodes.Common.InternalError);
        Assert.Contains(LastReserveAttempt, fixture.Inventory.Reserved.Select(reservation => reservation.OrderId));
        Assert.Contains(LastReserveAttempt, fixture.Inventory.Released);
        await AssertNotSavedAsync(LastReserveAttempt);
    }

    [Fact]
    public async Task ReleaseTheWarehouseCannotTakeIsQueuedAndTheWorkerFinishesIt()
    {
        fixture.Inventory.ReserveFailure = () => new InventoryTimeoutException("The warehouse missed the deadline.");
        fixture.Inventory.ReleaseFailure = () => new InventoryUnavailableException("The warehouse is down.");

        using var response = await PlaceLampOrderAsync();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var orderId = LastReserveAttempt;
        var queued = await PendingReleaseAsync(orderId);
        Assert.NotNull(queued);
        Assert.Equal(0, queued.Attempts);
        Assert.Equal("The warehouse is down.", queued.LastError);

        // The warehouse is back
        fixture.Inventory.ReleaseFailure = null;
        await ProcessDueAsync();

        Assert.Contains(orderId, fixture.Inventory.Released);
        Assert.Null(await PendingReleaseAsync(orderId));
    }

    [Fact]
    public async Task CancellingWhileTheWarehouseIsDownSucceedsAndQueuesTheRelease()
    {
        var orderId = await fixture.PlaceOrderAsync(OrdersApiFixture.CustomerA);
        fixture.Inventory.ReleaseFailure = () => new InventoryUnavailableException("The warehouse is down.");
        using var admin = fixture.AdminClient();

        using var response = await admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status = "Cancelled" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(orderId, fixture.Inventory.Released);
        Assert.NotNull(await PendingReleaseAsync(orderId));
    }

    [Fact]
    public async Task WorkerBacksOffWhileTheWarehouseStaysDown()
    {
        var orderId = Guid.CreateVersion7();
        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            context.PendingReleases.Add(new PendingRelease(orderId, "Queued by the test.", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        fixture.Inventory.ReleaseFailure = () => new InventoryUnavailableException("Still down.");

        await ProcessDueAsync();
        var afterFirstRetry = await PendingReleaseAsync(orderId);
        await ProcessDueAsync();

        Assert.NotNull(afterFirstRetry);
        Assert.Equal(1, afterFirstRetry.Attempts);
        Assert.Equal("Still down.", afterFirstRetry.LastError);
        Assert.True(afterFirstRetry.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(4), "The next retry should wait about 5 s");

        // The second pass found it not yet due, so it didn't call the warehouse again
        Assert.Single(fixture.Inventory.ReleaseAttempts, id => id == orderId);
    }

    public void Dispose()
    {
        fixture.Inventory.Reset();
        fixture.SaveFailure.FailNextOrderSave = false;
    }

    private async Task<HttpResponseMessage> PlaceLampOrderAsync(int quantity = 1)
    {
        using var client = fixture.Customer(OrdersApiFixture.CustomerB);
        return await client.PostAsJsonAsync("/api/v1/orders",
            new { lines = new[] { new { productId = FakeProductCatalog.Lamp.Id, quantity } } });
    }

    private async Task AssertNotSavedAsync(Guid orderId)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Orders.AnyAsync(order => order.Id == orderId));
    }

    private async Task<PendingRelease?> PendingReleaseAsync(Guid orderId)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().PendingReleases.AsNoTracking()
            .SingleOrDefaultAsync(pending => pending.OrderId == orderId);
    }

    private async Task ProcessDueAsync()
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PendingReleaseProcessor>().ProcessDueAsync(CancellationToken.None);
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
