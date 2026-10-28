using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Products;
using Shop.Modules.Products.Contracts;
using Xunit;

namespace Shop.Modules.Products.UnitTests;

public sealed class InventoryServiceTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly IInventoryService _inventory;

    public InventoryServiceTests()
    {
        // Resolve the public contract through module registration, without a database or internal types.
        _provider = new ServiceCollection().AddProductsModule()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scope = _provider.CreateScope();
        _inventory = _scope.ServiceProvider.GetRequiredService<IInventoryService>();
    }

    [Fact]
    public async Task ProductWithoutStockDataHasUnlimitedAvailability()
    {
        var available = await _inventory.GetAvailableAsync(Guid.CreateVersion7(), CancellationToken.None);

        Assert.Equal(int.MaxValue, available);
    }

    [Fact]
    public async Task ReservationAcceptsAllLinesWithoutChangingTheirAvailability()
    {
        var lines = new[]
        {
            new StockLine(Guid.CreateVersion7(), 2),
            new StockLine(Guid.CreateVersion7(), 5),
        };

        var result = await _inventory.ReserveAsync(Guid.CreateVersion7(), lines, CancellationToken.None);

        Assert.True(result.Success);
        foreach (var line in lines)
        {
            Assert.Equal(int.MaxValue, await _inventory.GetAvailableAsync(line.ProductId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task RetryingReservationAndReleaseIsSafe()
    {
        var orderId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var lines = new[] { new StockLine(productId, 1) };

        var first = await _inventory.ReserveAsync(orderId, lines, CancellationToken.None);
        var retry = await _inventory.ReserveAsync(orderId, lines, CancellationToken.None);
        await _inventory.ReleaseAsync(orderId, CancellationToken.None);
        await _inventory.ReleaseAsync(orderId, CancellationToken.None);

        Assert.True(first.Success);
        Assert.Equal(first, retry);
        Assert.Equal(int.MaxValue, await _inventory.GetAvailableAsync(productId, CancellationToken.None));
    }

    [Fact]
    public async Task ReleasingAnUnknownOrderIsSafe()
    {
        await _inventory.ReleaseAsync(Guid.CreateVersion7(), CancellationToken.None);
    }

    [Theory]
    [InlineData("availability")]
    [InlineData("reserve")]
    [InlineData("release")]
    public async Task InventoryOperationsHonorCancellation(string operation)
    {
        var ct = new CancellationToken(canceled: true);
        var orderId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "availability" => _inventory.GetAvailableAsync(productId, ct),
            "reserve" => _inventory.ReserveAsync(orderId, new[] { new StockLine(productId, 1) }, ct),
            "release" => _inventory.ReleaseAsync(orderId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });

        Assert.Equal(ct, error.CancellationToken);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
