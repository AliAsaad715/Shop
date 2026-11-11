using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shop.Modules.Products.Catalog;
using Shop.Modules.Products.Data;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Persistence;

namespace Shop.Modules.Products.Updates;

internal static class UpdateProductEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/products/{id}", HandleAsync)
            .WithName("UpdateProduct")
            .WithTags("Products")
            .WithSummary("Replace a product's name, description, price and category; keep its existing image")
            .WithDescription("All four JSON fields are required. Returns 204 after saving and invalidating catalog caches. "
                + "Overlapping saves return 409; If-Match is not supported.")
            .RequireAuthorization(Policies.AdminOnly)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

    public static async Task<Results<NoContent, ProblemHttpResult>> HandleAsync(
        Guid id, UpdateProductRequest request, ProductsDbContext context, CatalogCacheInvalidator cache,
        HttpResponse response, DatabaseReadRouter routing, CancellationToken ct)
    {
        response.Headers.CacheControl = "no-store";
        // Read the tracked row from the primary, never from the catalog cache. Its xmin protects the save.
        var product = await context.Products.SingleOrDefaultAsync(product => product.Id == id, ct);
        if (product is null)
        {
            return Problems.Create(StatusCodes.Status404NotFound, ErrorCodes.Products.NotFound, "Product not found");
        }

        var oldCategoryId = product.CategoryId;
        if (!await context.Categories.AsNoTracking().AnyAsync(category => category.Id == request.CategoryId, ct))
        {
            return UnknownCategory();
        }

        product.Name = request.Name.Trim();
        product.Description = request.Description.Trim();
        product.Price = request.Price!.Value;
        product.CategoryId = request.CategoryId!.Value;

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // The selected category can disappear between the check and the save.
            return UnknownCategory();
        }

        // The commit succeeded: use the invalidator's independent timeout even if the client disconnects.
        await routing.MarkWrittenAsync();
        await cache.ProductChangedAsync(product.Id, oldCategoryId, product.CategoryId);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult UnknownCategory() =>
        Problems.Validation(new Dictionary<string, string[]> { ["categoryId"] = ["The selected category does not exist."] });
}
