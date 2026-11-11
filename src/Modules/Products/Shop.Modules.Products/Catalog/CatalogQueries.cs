using Microsoft.EntityFrameworkCore;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Domain;

namespace Shop.Modules.Products.Catalog;

internal static class CatalogQueries
{
    public static async Task<ProductPageResponse> ListAsync(ListProductsRequest request, ProductsDbContext context, CancellationToken ct)
    {
        var products = context.Products.AsNoTracking();
        if (request.Category is { } category) products = products.Where(product => product.CategoryId == category);
        if (ProductCursor.TryDecode(request.Cursor, out var after))
        {
            var following = products.Where(product => EF.Functions.LessThan(
                ValueTuple.Create(product.CreatedAt, product.Id), ValueTuple.Create(after.CreatedAt, after.Id)));
            var rows = await Responses(Newest(following).Take(request.PageSize + 1)).ToListAsync(ct);
            var page = rows.Take(request.PageSize).ToList();
            var next = rows.Count > request.PageSize ? ProductCursor.After(page[^1]).Encode() : null;
            return new ProductPageResponse(page, null, request.PageSize, null, next);
        }
        var number = request.Page ?? 1;
        var totalCount = await products.CountAsync(ct);
        var items = await Responses(Newest(products).Skip((int)(((long)number - 1) * request.PageSize)).Take(request.PageSize)).ToListAsync(ct);
        var nextCursor = items.Count > 0 && (long)number * request.PageSize < totalCount ? ProductCursor.After(items[^1]).Encode() : null;
        return new ProductPageResponse(items, number, request.PageSize, totalCount, nextCursor);
    }

    public static Task<ProductResponse?> GetAsync(Guid id, ProductsDbContext context, CancellationToken ct) =>
        Responses(context.Products.AsNoTracking().Where(product => product.Id == id)).SingleOrDefaultAsync(ct);

    public static Task<List<CategoryResponse>> CategoriesAsync(ProductsDbContext context, CancellationToken ct) =>
        context.Categories.AsNoTracking().OrderBy(category => category.Name).ThenBy(category => category.Id)
            .Select(category => new CategoryResponse(category.Id, category.Name)).ToListAsync(ct);

    private static IOrderedQueryable<Product> Newest(IQueryable<Product> products) =>
        products.OrderByDescending(product => product.CreatedAt).ThenByDescending(product => product.Id);

    private static IQueryable<ProductResponse> Responses(IQueryable<Product> products) =>
        products.Select(product => new ProductResponse(
            product.Id, product.Name, product.Description, product.Price, ImageUrl(product.ImagePath),
            product.CategoryId, product.Category.Name, product.CreatedAt));

    private static string ImageUrl(string imagePath) =>
        "/images/" + string.Join('/', imagePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
