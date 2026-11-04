using Grpc.Core;
using Grpc.Health.V1;
using Grpc.Net.Client.Configuration;
using Inventory.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shop.Modules.Products.Contracts;

namespace Shop.Modules.Products.Inventory;

internal static class InventoryRegistration
{
    /// <summary>
    /// Registers <see cref="IInventoryService"/> as a gRPC client of the warehouse. A missing or invalid
    /// <c>Inventory</c> section stops the app at startup.
    /// </summary>
    public static IServiceCollection AddInventoryClient(this IServiceCollection services)
    {
        services.AddOptions<InventoryOptions>()
            .BindConfiguration(InventoryOptions.Section)
            .Validate(InventoryOptions.HasValidAddress,
                "Set Inventory__Address to the warehouse's http:// or https:// address, such as http://inventory:50051.")
            .Validate(InventoryOptions.HasValidTimeouts,
                "Inventory:GetStockTimeout, ReserveTimeout and ReleaseTimeout must each be more than 0 and at most 30 seconds.")
            .ValidateOnStart();

        services.AddGrpcClient<InventoryService.InventoryServiceClient>((provider, client) =>
                client.Address = provider.GetRequiredService<IOptions<InventoryOptions>>().Value.Address)
            .ConfigureChannel(channel => channel.ServiceConfig = RetryOnUnavailable());

        services.AddScoped<IInventoryService, GrpcInventoryService>();

        // The standard gRPC health service the warehouse serves beside the contract. Degraded, not Unhealthy:
        // browsing works without the warehouse; only stock and orders need it.
        services.AddGrpcClient<Health.HealthClient>((provider, client) =>
            client.Address = provider.GetRequiredService<IOptions<InventoryOptions>>().Value.Address);
        services.AddHealthChecks().AddCheck<WarehouseHealthCheck>("warehouse", failureStatus: HealthStatus.Degraded);

        return services;
    }

    // UNAVAILABLE is a transport failure, and order_id makes all three RPCs safe to repeat. The call's
    // deadline still bounds every attempt, so a retry never makes the caller wait longer.
    private static ServiceConfig RetryOnUnavailable() => new()
    {
        MethodConfigs =
        {
            new MethodConfig
            {
                Names = { MethodName.Default },
                RetryPolicy = new RetryPolicy
                {
                    MaxAttempts = 3,
                    InitialBackoff = TimeSpan.FromMilliseconds(100),
                    MaxBackoff = TimeSpan.FromMilliseconds(400),
                    BackoffMultiplier = 2,
                    RetryableStatusCodes = { StatusCode.Unavailable },
                },
            },
        },
    };
}
