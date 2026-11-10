using Microsoft.AspNetCore.Mvc;

namespace Shop.Modules.Products.Catalog;

// A numbered page (page, 1 unless given) or the page after a cursor from an earlier response, never both
internal sealed record ListProductsRequest(
    [property: FromQuery(Name = "category")] Guid? Category = null,
    [property: FromQuery(Name = "page")] int? Page = null,
    [property: FromQuery(Name = "pageSize")] int PageSize = 20,
    [property: FromQuery(Name = "cursor")] string? Cursor = null);
