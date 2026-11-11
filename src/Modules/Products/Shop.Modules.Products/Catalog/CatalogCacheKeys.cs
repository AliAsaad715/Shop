using System.Globalization;

namespace Shop.Modules.Products.Catalog;

internal static class CatalogCacheKeys
{
    public const string CatalogTag = "catalog";
    public const string Categories = "categories";
    public const string AllProductsTag = "products:all";
    public static string Product(Guid id) => $"product:{id:D}";
    public static string CategoryTag(Guid id) => $"products:category:{id:D}";
    public static string List(ListProductsRequest request) => string.Create(CultureInfo.InvariantCulture,
        $"products:{request.Category?.ToString("D") ?? "all"}:page:{request.Page ?? 1}:size:{request.PageSize}");
}
