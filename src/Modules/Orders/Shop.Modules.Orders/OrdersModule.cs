using FluentValidation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shop.Modules.Orders.Data;
using Shop.Modules.Orders.Placement;
using Shop.Modules.Orders.Reading;
using Shop.Modules.Orders.StatusChanges;
using Shop.SharedKernel.Persistence;

namespace Shop.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<OrdersDbContext>(OrdersDbContext.Schema);
        services.TryAddSingleton(TimeProvider.System);

        // One scan registers every validator in the module (placement, reading and status changes alike)
        services.AddValidatorsFromAssemblyContaining<CreateOrderRequestValidator>(includeInternalTypes: true);

        return services;
    }

    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        CreateOrderEndpoint.Map(endpoints);
        OrderEndpoints.Map(endpoints);
        UpdateOrderStatusEndpoint.Map(endpoints);

        return endpoints;
    }
}
