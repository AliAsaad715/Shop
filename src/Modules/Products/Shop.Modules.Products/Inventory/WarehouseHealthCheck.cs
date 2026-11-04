using Grpc.Core;
using Grpc.Health.V1;
using Inventory.V1;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Shop.Modules.Products.Inventory;

/// <summary>
/// Asks the warehouse's standard gRPC health service whether the inventory service is serving. It is
/// registered to report Degraded on failure, so a missing warehouse never makes the app Unhealthy.
/// </summary>
internal sealed class WarehouseHealthCheck(Health.HealthClient client, IOptions<InventoryOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // As cheap as a stock lookup, so it gets the same deadline
            var response = await client.CheckAsync(
                new HealthCheckRequest { Service = InventoryService.Descriptor.FullName },
                deadline: DateTime.UtcNow.Add(options.Value.GetStockTimeout),
                cancellationToken: cancellationToken);

            return response.Status == HealthCheckResponse.Types.ServingStatus.Serving
                ? HealthCheckResult.Healthy("The warehouse is serving.")
                : new HealthCheckResult(context.Registration.FailureStatus, $"The warehouse reports {response.Status}.");
        }
        catch (RpcException error)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus, $"The warehouse can't be reached ({error.StatusCode}).", error);
        }
    }
}
