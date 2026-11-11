using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Shop.Modules.Products.Contracts;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Domain;
using Shop.SharedKernel.Caching;
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
            .WithSummary("Get a product, its image URL and its stock from the warehouse (stockStatus unknown when the warehouse can't answer). "
                + "Send its ETag back in If-None-Match to get a 304 with no body while nothing in it has changed")
            .AllowAnonymous()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet("/categories", CategoriesAsync)
            .WithName("ListCategories")
            .WithTags("Products")
            .WithSummary("List product categories")
            .AllowAnonymous();
    }

    public static async Task<Ok<ProductPageResponse>> ListAsync(
        [AsParameters] ListProductsRequest request, ProductsDbContext context, CatalogCache catalog, HttpResponse response,
        CancellationToken ct)
    {
        // Keyset: the page starts right after the cursor's product, where the index finds it directly, so a deep page
        // reads only its own rows and needs no count. One row more than the page says whether another page follows
        if (ProductCursor.TryDecode(request.Cursor, out var after))
        {
            var following = InList(context, request.Category).Where(product => EF.Functions.LessThan(
                ValueTuple.Create(product.CreatedAt, product.Id), ValueTuple.Create(after.CreatedAt, after.Id)));
            var rows = await Responses(Newest(following).Take(request.PageSize + 1)).ToListAsync(ct);
            var page = rows.Take(request.PageSize).ToList();
            var next = rows.Count > request.PageSize ? ProductCursor.After(page[^1]).Encode() : null;
            return TypedResults.Ok(new ProductPageResponse(page, null, request.PageSize, null, next));
        }

        var number = request.Page ?? 1;
        if (number > 1)
        {
            return TypedResults.Ok(await NumberedPageAsync(context, request.Category, number, request.PageSize, ct));
        }

        // Most browsing stops at a list's first page, so that page comes from the cache (task R3-07)
        var first = await catalog.FirstPageAsync(request.Category, request.PageSize,
            (db, token) => NumberedPageAsync(db, request.Category, 1, request.PageSize, token), ct);
        response.MarkCache(first.Hit);
        return TypedResults.Ok(first.Value);
    }

    private static async Task<ProductPageResponse> NumberedPageAsync(
        ProductsDbContext context, Guid? category, int number, int pageSize, CancellationToken ct)
    {
        var products = InList(context, category);
        var totalCount = await products.CountAsync(ct);
        var items = await Responses(Newest(products).Skip((int)(((long)number - 1) * pageSize)).Take(pageSize))
            .ToListAsync(ct);
        var nextCursor = items.Count > 0 && (long)number * pageSize < totalCount
            ? ProductCursor.After(items[^1]).Encode()
            : null;

        return new ProductPageResponse(items, number, pageSize, totalCount, nextCursor);
    }

    private static IQueryable<Product> InList(ProductsDbContext context, Guid? category) =>
        category is { } id
            ? context.Products.AsNoTracking().Where(product => product.CategoryId == id)
            : context.Products.AsNoTracking();

    // Newest first, the ID breaking ties in the same direction: then "after this product" is one row comparison on
    // (created_at, id) that walks the (category_id, created_at DESC, id DESC) index from the right place
    private static IOrderedQueryable<Product> Newest(IQueryable<Product> products) =>
        products.OrderByDescending(product => product.CreatedAt).ThenByDescending(product => product.Id);

    public static async Task<Results<Ok<ProductDetailsResponse>, StatusCodeHttpResult, ProblemHttpResult>> GetAsync(
        Guid id, CatalogCache catalog, IInventoryService inventory, HttpRequest request, HttpResponse response,
        CancellationToken ct)
    {
        // Everything but the stock comes from the cache (task R3-07), a product that doesn't exist included; the
        // stock always comes from the warehouse
        var cached = await catalog.ProductAsync(id,
            (db, token) => Versioned(db.Products.AsNoTracking().Where(product => product.Id == id)).SingleOrDefaultAsync(token), ct);
        response.MarkCache(cached.Hit);
        if (cached.Value is not { } found)
        {
            return Problems.Create(StatusCodes.Status404NotFound, ErrorCodes.Products.NotFound, "Product not found");
        }

        var (stockStatus, available) = await StockAsync(inventory, id, ct);

        // The client may keep the response but must check with us every time (no-cache), and while nothing in it has
        // changed, that check is a 304 with no body; both carry the ETag (task R3-09). The product and the stock are
        // still read: a 304 saves the transfer and the serialization
        var etag = ProductETag.For(found.Version, found.CategoryVersion, available);
        response.Headers.ETag = etag.ToString();
        response.Headers.CacheControl = "no-cache";
        if (ProductETag.Matches(request.GetTypedHeaders().IfNoneMatch, etag))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return TypedResults.Ok(ProductDetailsResponse.From(found.Product, stockStatus, available));
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

    public static async Task<Ok<IReadOnlyList<CategoryResponse>>> CategoriesAsync(
        CatalogCache catalog, HttpResponse response, CancellationToken ct)
    {
        var categories = await catalog.CategoriesAsync(async (db, token) => new CategoryList(await db.Categories.AsNoTracking()
            .OrderBy(category => category.Name).ThenBy(category => category.Id)
            .Select(category => new CategoryResponse(category.Id, category.Name))
            .ToListAsync(token)), ct);
        response.MarkCache(categories.Hit);
        return TypedResults.Ok(categories.Value.Items);
    }

    private static IQueryable<ProductResponse> Responses(IQueryable<Product> products) =>
        products.Select(product => new ProductResponse(
            product.Id, product.Name, product.Description, product.Price, ImageUrl(product.ImagePath),
            product.CategoryId, product.Category.Name, product.CreatedAt));

    // The same response as Responses, with the row versions the details' ETag is built from, in the same query
    private static IQueryable<CachedProduct> Versioned(IQueryable<Product> products) =>
        products.Select(product => new CachedProduct(
            new ProductResponse(
                product.Id, product.Name, product.Description, product.Price, ImageUrl(product.ImagePath),
                product.CategoryId, product.Category.Name, product.CreatedAt),
            product.Version, product.Category.Version));

    // Stored paths are relative to the image root; task 15 provides storage and serving at /images.
    private static string ImageUrl(string imagePath) =>
        "/images/" + string.Join('/', imagePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
