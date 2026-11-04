using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Shop.Modules.Products.UnitTests;

/// <summary>A missing or wrong <c>Inventory</c> section stops the app at startup, not at its first order.</summary>
public sealed class InventoryStartupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("inventory:50051")]
    [InlineData("/inventory")]
    [InlineData("ftp://inventory:50051")]
    public async Task AppWithoutAnHttpWarehouseAddressDoesNotStart(string? address)
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            StartAsync(new Dictionary<string, string?> { ["Inventory:Address"] = address }));

        Assert.Contains("Inventory__Address", error.Message);
    }

    [Theory]
    [InlineData("Inventory:GetStockTimeout", "00:00:00")]
    [InlineData("Inventory:ReserveTimeout", "-00:00:01")]
    [InlineData("Inventory:ReleaseTimeout", "00:01:00")]
    public async Task AppWithATimeoutOutOfRangeDoesNotStart(string key, string value)
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(new Dictionary<string, string?>
        {
            ["Inventory:Address"] = "http://inventory:50051",
            [key] = value,
        }));

        Assert.Contains("at most 30 seconds", error.Message);
    }

    [Fact]
    public async Task AppWithAWarehouseAddressStarts()
    {
        await StartAsync(new Dictionary<string, string?> { ["Inventory:Address"] = "http://inventory:50051" });
    }

    private static async Task StartAsync(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddProductsModule();

        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();
    }
}
