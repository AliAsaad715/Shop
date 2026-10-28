using FluentValidation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Products.Catalog;
using Shop.Modules.Products.Contracts;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Inventory;
using Shop.SharedKernel.Persistence;

namespace Shop.Modules.Products;

public static class ProductsModule
{
    public static IServiceCollection AddProductsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<ProductsDbContext>(ProductsDbContext.Schema);
        services.AddScoped<IInventoryService, PlaceholderInventoryService>();
        services.AddScoped<IProductCatalog, ProductCatalog>();
        services.AddValidatorsFromAssemblyContaining<ListProductsRequestValidator>(includeInternalTypes: true);

        return services;
    }

    public static IEndpointRouteBuilder MapProductsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ProductEndpoints.Map(endpoints);

        // POST /products arrives with task 15.

        return endpoints;
    }
}
