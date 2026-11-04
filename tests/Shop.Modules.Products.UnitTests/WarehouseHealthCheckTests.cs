using Grpc.Health.V1;
using Inventory.V1;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Shop.Modules.Products.UnitTests;

/// <summary>
/// The app's health follows the warehouse's standard gRPC health service, and a missing warehouse makes
/// it Degraded, never Unhealthy, so browsing keeps working.
/// </summary>
public sealed class WarehouseHealthCheckTests
{
    [Fact]
    public async Task ServingWarehouseIsHealthy()
    {
        await using var testbed = await InventoryTestbed.StartAsync();

        var health = await testbed.CheckWarehouseHealthAsync();

        Assert.Equal(HealthStatus.Healthy, health.Status);
    }

    [Fact]
    public async Task WarehouseThatIsNotServingMakesTheAppDegraded()
    {
        await using var testbed = await InventoryTestbed.StartAsync();
        testbed.WarehouseHealth.SetStatus(InventoryService.Descriptor.FullName, HealthCheckResponse.Types.ServingStatus.NotServing);

        var health = await testbed.CheckWarehouseHealthAsync();

        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Contains("NotServing", health.Description);
    }

    [Fact]
    public async Task StoppedWarehouseMakesTheAppDegradedNotUnhealthy()
    {
        await using var testbed = InventoryTestbed.WithStoppedWarehouse();

        var health = await testbed.CheckWarehouseHealthAsync();

        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Contains("can't be reached", health.Description);
    }
}
