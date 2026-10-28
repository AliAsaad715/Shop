using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Products.Contracts;
using Shop.Modules.Products.Creation;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Images;
using Shop.Modules.Products.Inventory;
using Shop.SharedKernel.Persistence;

namespace Shop.Modules.Products;

public static class ProductsModule
{
    public static IServiceCollection AddProductsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<ProductsDbContext>(ProductsDbContext.Schema);
        services.AddScoped<IInventoryService, PlaceholderInventoryService>();
        services.AddScoped<IImageStorage, LocalImageStorage>();
        services.AddOptions<ImageStorageOptions>().BindConfiguration("Images")
            .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath), "Images:RootPath is required.");
        services.AddValidatorsFromAssemblyContaining<CreateProductRequestValidator>(includeInternalTypes: true);

        return services;
    }

    public static IEndpointRouteBuilder MapProductsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        CreateProductEndpoint.Map(endpoints);

        // Product and category reads arrive with task 16.

        return endpoints;
    }

    public static IApplicationBuilder UseProductUploads(this IApplicationBuilder app) =>
        app.UseMiddleware<ProductUploadLimitMiddleware>();

    public static IEndpointRouteBuilder MapProductImages(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/images/products/{fileName}", ProductImageEndpoint.HandleAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        return endpoints;
    }
}
