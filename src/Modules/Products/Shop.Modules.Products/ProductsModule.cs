using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Products.Data;
using Shop.SharedKernel.Persistence;

namespace Shop.Modules.Products;

public static class ProductsModule
{
    public static IServiceCollection AddProductsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<ProductsDbContext>(ProductsDbContext.Schema);

        return services;
    }

    public static IEndpointRouteBuilder MapProductsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // POST /products, GET /products, GET /products/{id} and GET /categories arrive with tasks 15 and 16

        return endpoints;
    }
}
