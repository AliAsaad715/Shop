using System.Diagnostics;
using Grpc.Core;
using Inventory.V1;
using Shop.Modules.Products.Contracts;
using Xunit;

namespace Shop.Modules.Products.UnitTests;

/// <summary>
/// The warehouse client through the public <see cref="IInventoryService"/>: what it sends, the deadline on
/// each call, retries on UNAVAILABLE, and how gRPC failures become inventory errors.
/// </summary>
public sealed class GrpcInventoryServiceTests
{
    // Long enough that only the deadline tests ever reach one
    private static readonly Dictionary<string, string?> Patient = new()
    {
        ["Inventory:GetStockTimeout"] = "00:00:05",
        ["Inventory:ReserveTimeout"] = "00:00:05",
        ["Inventory:ReleaseTimeout"] = "00:00:05",
    };

    [Fact]
    public async Task GetAvailableAsksTheWarehouseAboutTheProduct()
    {
        await using var testbed = await InventoryTestbed.StartAsync(Patient);
        var productId = Guid.CreateVersion7();
        testbed.Warehouse.OnGetStock = (_, _) => Task.FromResult(new GetStockResponse { Available = 7 });

        var available = await testbed.Inventory.GetAvailableAsync(productId, CancellationToken.None);

        Assert.Equal(7, available);
        var request = Assert.IsType<GetStockRequest>(Assert.Single(testbed.Warehouse.Calls).Request);
        Assert.Equal(productId.ToString(), request.ProductId);
    }

    [Fact]
    public async Task ReserveSendsTheOrderWithEveryLine()
    {
        await using var testbed = await InventoryTestbed.StartAsync(Patient);
        var orderId = Guid.CreateVersion7();
        var lines = new[] { new StockLine(Guid.CreateVersion7(), 2), new StockLine(Guid.CreateVersion7(), 5) };

        var result = await testbed.Inventory.ReserveAsync(orderId, lines, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Shortages);
        var request = Assert.IsType<ReserveRequest>(Assert.Single(testbed.Warehouse.Calls).Request);
        Assert.Equal(orderId.ToString(), request.OrderId);
        Assert.Equal(
            lines.Select(line => (line.ProductId.ToString(), line.Quantity)),
            request.Items.Select(item => (item.ProductId, item.Quantity)));
    }

    [Fact]
    public async Task RefusedReservationCarriesTheShortages()
    {
        await using var testbed = await InventoryTestbed.StartAsync(Patient);
        var shortProduct = Guid.CreateVersion7();
        testbed.Warehouse.OnReserve = (_, _) => Task.FromResult(new ReserveResponse
        {
            Reserved = false,
            Shortages = { new Shortage { ProductId = shortProduct.ToString(), Requested = 3, Available = 1 } },
        });

        var result = await testbed.Inventory.ReserveAsync(
            Guid.CreateVersion7(), [new StockLine(shortProduct, 3)], CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new[] { new StockShortage(shortProduct, 3, 1) }, result.Shortages);
    }

    [Fact]
    public async Task ReleaseSendsTheOrder()
    {
        await using var testbed = await InventoryTestbed.StartAsync(Patient);
        var orderId = Guid.CreateVersion7();

        await testbed.Inventory.ReleaseAsync(orderId, CancellationToken.None);

        var request = Assert.IsType<ReleaseRequest>(Assert.Single(testbed.Warehouse.Calls).Request);
        Assert.Equal(orderId.ToString(), request.OrderId);
    }

    [Fact]
    public async Task EachCallCarriesItsOwnConfiguredDeadline()
    {
        // Far enough apart that the warehouse can tell which setting each call used
        await using var testbed = await InventoryTestbed.StartAsync(new Dictionary<string, string?>
        {
            ["Inventory:GetStockTimeout"] = "00:00:03",
            ["Inventory:ReserveTimeout"] = "00:00:06",
            ["Inventory:ReleaseTimeout"] = "00:00:09",
        });

        await testbed.Inventory.GetAvailableAsync(Guid.CreateVersion7(), CancellationToken.None);
        await testbed.Inventory.ReserveAsync(Guid.CreateVersion7(), [new StockLine(Guid.CreateVersion7(), 1)], CancellationToken.None);
        await testbed.Inventory.ReleaseAsync(Guid.CreateVersion7(), CancellationToken.None);

        AssertTimeLeft(testbed.Warehouse.CallsTo("GetStock"), TimeSpan.FromSeconds(3));
        AssertTimeLeft(testbed.Warehouse.CallsTo("Reserve"), TimeSpan.FromSeconds(6));
        AssertTimeLeft(testbed.Warehouse.CallsTo("Release"), TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task WithoutSettingsTheDeadlinesAreThePlansStartingValues()
    {
        await using var testbed = await InventoryTestbed.StartAsync();

        // The first call also opens the connection; measure the ones after it
        await testbed.Inventory.ReleaseAsync(Guid.CreateVersion7(), CancellationToken.None);
        await testbed.Inventory.GetAvailableAsync(Guid.CreateVersion7(), CancellationToken.None);
        await testbed.Inventory.ReserveAsync(Guid.CreateVersion7(), [new StockLine(Guid.CreateVersion7(), 1)], CancellationToken.None);

        AssertTimeLeft(testbed.Warehouse.CallsTo("GetStock"), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(250));
        AssertTimeLeft(testbed.Warehouse.CallsTo("Reserve"), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task UnavailableIsRetriedUntilTheWarehouseAnswers()
    {
        await using var testbed = await InventoryTestbed.StartAsync(Patient);
        var attempts = 0;
        testbed.Warehouse.OnGetStock = (_, _) => Interlocked.Increment(ref attempts) < 3
            ? throw new RpcException(new Status(StatusCode.Unavailable, "Starting up"))
            : Task.FromResult(new GetStockResponse { Available = 4 });

        var available = await testbed.Inventory.GetAvailableAsync(Guid.CreateVersion7(), CancellationToken.None);

        Assert.Equal(4, available);
        Assert.Equal(3, testbed.Warehouse.Calls.Count);
    }

    [Fact]
    public async Task WarehouseThatStaysUnavailableFailsAsUnavailableAfterThreeAttempts()
    {
        await using var testbed = await InventoryTestbed.StartAsync(Patient);
        testbed.Warehouse.OnReserve = (_, _) => throw new RpcException(new Status(StatusCode.Unavailable, "Down"));

        await Assert.ThrowsAsync<InventoryUnavailableException>(() => testbed.Inventory.ReserveAsync(
            Guid.CreateVersion7(), [new StockLine(Guid.CreateVersion7(), 1)], CancellationToken.None));

        Assert.Equal(3, testbed.Warehouse.Calls.Count);
    }

    [Fact]
    public async Task StoppedWarehouseFailsAsUnavailable()
    {
        await using var testbed = InventoryTestbed.WithStoppedWarehouse(Patient);

        await Assert.ThrowsAsync<InventoryUnavailableException>(() => testbed.Inventory.ReserveAsync(
            Guid.CreateVersion7(), [new StockLine(Guid.CreateVersion7(), 1)], CancellationToken.None));
    }

    [Fact]
    public async Task SlowWarehouseFailsAsTimeoutAtTheDeadline()
    {
        await using var testbed = await InventoryTestbed.StartAsync(new Dictionary<string, string?>
        {
            ["Inventory:ReserveTimeout"] = "00:00:00.200",
        });
        testbed.Warehouse.OnReserve = async (_, context) =>
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return new ReserveResponse { Reserved = true };
        };
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<InventoryTimeoutException>(() => testbed.Inventory.ReserveAsync(
            Guid.CreateVersion7(), [new StockLine(Guid.CreateVersion7(), 1)], CancellationToken.None));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Gave up after {clock.Elapsed}, long after the 200 ms deadline");
    }

    [Fact]
    public async Task RejectedRequestFailsAsAnInventoryErrorWithoutRetrying()
    {
        await using var testbed = await InventoryTestbed.StartAsync(Patient);
        testbed.Warehouse.OnReserve = (_, _) =>
            throw new RpcException(new Status(StatusCode.InvalidArgument, "quantity must be greater than 0"));

        var error = await Assert.ThrowsAsync<InventoryException>(() => testbed.Inventory.ReserveAsync(
            Guid.CreateVersion7(), [new StockLine(Guid.CreateVersion7(), 0)], CancellationToken.None));

        Assert.Contains(nameof(StatusCode.InvalidArgument), error.Message);
        Assert.Single(testbed.Warehouse.Calls);
    }

    [Fact]
    public async Task ShortageForSomethingThatIsNotAProductFailsAsAnInventoryError()
    {
        await using var testbed = await InventoryTestbed.StartAsync(Patient);
        testbed.Warehouse.OnReserve = (_, _) => Task.FromResult(new ReserveResponse
        {
            Reserved = false,
            Shortages = { new Shortage { ProductId = "not-a-product", Requested = 1, Available = 0 } },
        });

        await Assert.ThrowsAsync<InventoryException>(() => testbed.Inventory.ReserveAsync(
            Guid.CreateVersion7(), [new StockLine(Guid.CreateVersion7(), 1)], CancellationToken.None));
    }

    [Theory]
    [InlineData("availability")]
    [InlineData("reserve")]
    [InlineData("release")]
    public async Task CancelledCallsThrowOperationCanceled(string operation)
    {
        await using var testbed = await InventoryTestbed.StartAsync(Patient);
        var ct = new CancellationToken(canceled: true);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "availability" => testbed.Inventory.GetAvailableAsync(Guid.CreateVersion7(), ct),
            "reserve" => testbed.Inventory.ReserveAsync(Guid.CreateVersion7(), [new StockLine(Guid.CreateVersion7(), 1)], ct),
            "release" => testbed.Inventory.ReleaseAsync(Guid.CreateVersion7(), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });

        Assert.Equal(ct, error.CancellationToken);
    }

    private static void AssertTimeLeft(IReadOnlyList<WarehouseCall> calls, TimeSpan configured, TimeSpan? slack = null)
    {
        var timeLeft = Assert.Single(calls).TimeLeft;

        Assert.InRange(timeLeft, configured - (slack ?? TimeSpan.FromSeconds(1)), configured + TimeSpan.FromMilliseconds(50));
    }
}
