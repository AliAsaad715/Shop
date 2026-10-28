using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Orders.Data;
using Shop.SharedKernel.Persistence;

namespace Shop.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<OrdersDbContext>(OrdersDbContext.Schema);

        return services;
    }

    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // POST /orders, GET /orders, GET /orders/{id} and PATCH /orders/{id}/status arrive with tasks 19 to 21

        return endpoints;
    }
}
