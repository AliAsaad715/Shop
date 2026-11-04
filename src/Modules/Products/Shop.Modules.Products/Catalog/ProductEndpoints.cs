using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Shop.Modules.Products.Contracts;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Domain;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Paging;

namespace Shop.Modules.Products.Catalog;

internal static class ProductEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/products", ListAsync)
            .WithName("ListProducts")
            .WithTags("Products")
            .WithSummary("List products by category with paging (pageSize 1–50, default 20)")
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

    public static async Task<Ok<PagedResponse<ProductResponse>>> ListAsync(
        [AsParameters] ListProductsRequest request, ProductsDbContext context, CancellationToken ct)
    {
        var products = context.Products.AsNoTracking();
        if (request.Category is { } category)
        {
            products = products.Where(product => product.CategoryId == category);
        }

        var totalCount = await products.CountAsync(ct);
        var page = products.OrderByDescending(product => product.CreatedAt).ThenBy(product => product.Id)
            .Skip((int)(((long)request.Page - 1) * request.PageSize)).Take(request.PageSize);
        var items = await Responses(page).ToListAsync(ct);

        return TypedResults.Ok(new PagedResponse<ProductResponse>(items, request.Page, request.PageSize, totalCount));
    }

    public static async Task<Results<Ok<ProductDetailsResponse>, ProblemHttpResult>> GetAsync(
        Guid id, ProductsDbContext context, IInventoryService inventory, CancellationToken ct)
    {
        var product = await Responses(context.Products.AsNoTracking().Where(product => product.Id == id)).SingleOrDefaultAsync(ct);
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

    public static async Task<Ok<List<CategoryResponse>>> CategoriesAsync(ProductsDbContext context, CancellationToken ct) =>
        TypedResults.Ok(await context.Categories.AsNoTracking()
            .OrderBy(category => category.Name).ThenBy(category => category.Id)
            .Select(category => new CategoryResponse(category.Id, category.Name))
            .ToListAsync(ct));

    private static IQueryable<ProductResponse> Responses(IQueryable<Product> products) =>
        products.Select(product => new ProductResponse(
            product.Id, product.Name, product.Description, product.Price, ImageUrl(product.ImagePath),
            product.CategoryId, product.Category.Name, product.CreatedAt));

    // Stored paths are relative to the image root; task 15 provides storage and serving at /images.
    private static string ImageUrl(string imagePath) =>
        "/images/" + string.Join('/', imagePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
