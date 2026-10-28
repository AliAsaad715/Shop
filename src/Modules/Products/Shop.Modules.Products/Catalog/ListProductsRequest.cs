using Microsoft.AspNetCore.Mvc;

namespace Shop.Modules.Products.Catalog;

internal sealed record ListProductsRequest(
    [property: FromQuery(Name = "category")] Guid? Category = null,
    [property: FromQuery(Name = "page")] int Page = 1,
    [property: FromQuery(Name = "pageSize")] int PageSize = 20);
