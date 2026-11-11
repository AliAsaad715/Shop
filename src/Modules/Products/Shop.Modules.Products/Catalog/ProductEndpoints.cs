using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Shop.Modules.Products.Contracts;
using Shop.SharedKernel.Errors;

namespace Shop.Modules.Products.Catalog;

internal static class ProductEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/products", ListAsync)
            .WithName("ListProducts")
            .WithTags("Products")
            .WithSummary("List products newest first, by category, a numbered page (page, with totalCount) or the page after "
                + "a nextCursor (cursor, as fast at any depth); pageSize 1–50, default 20")
            .AllowAnonymous();

        endpoints.MapGet("/products/{id}", GetAsync)
            .WithName("GetProduct")
            .WithTags("Products")
            .WithSummary("Get a product, its image URL and its stock from the warehouse (stockStatus unknown when the warehouse can't answer)")
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet("/categories", CategoriesAsync)
            .WithName("ListCategories")
            .WithTags("Products")
            .WithSummary("List product categories")
            .AllowAnonymous();
    }

    public static async Task<Ok<ProductPageResponse>> ListAsync(
        [AsParameters] ListProductsRequest request, CachedCatalogReader catalog, CancellationToken ct) =>
        TypedResults.Ok(await catalog.ListAsync(request, ct));

    public static async Task<Results<Ok<ProductDetailsResponse>, ProblemHttpResult>> GetAsync(
        Guid id, CachedCatalogReader catalog, IInventoryService inventory, CancellationToken ct)
    {
        var product = await catalog.GetAsync(id, ct);
        if (product is null)
        {
            return Problems.Create(StatusCodes.Status404NotFound, ErrorCodes.Products.NotFound, "Product not found");
        }

        var (stockStatus, available) = await StockAsync(inventory, id, ct);
        return TypedResults.Ok(ProductDetailsResponse.From(product, stockStatus, available));
    }

    // A warehouse that is down or slow costs the page its stock count, never the page itself;
    // the inventory client has already logged why
    private static async Task<(StockStatus Status, int? Available)> StockAsync(
        IInventoryService inventory, Guid productId, CancellationToken ct)
    {
        try
        {
            var available = await inventory.GetAvailableAsync(productId, ct);
            return (available > 0 ? StockStatus.InStock : StockStatus.OutOfStock, available);
        }
        catch (InventoryException)
        {
            return (StockStatus.Unknown, null);
        }
    }

    public static async Task<Ok<List<CategoryResponse>>> CategoriesAsync(CachedCatalogReader catalog, CancellationToken ct) =>
        TypedResults.Ok(await catalog.CategoriesAsync(ct));
}
