using Microsoft.AspNetCore.Mvc;
using Shop.Modules.Orders.Domain;

namespace Shop.Modules.Orders.Reading;

internal sealed record ListOrdersRequest(
    [property: FromQuery(Name = "status")] OrderStatus? Status = null,
    [property: FromQuery(Name = "page")] int Page = 1,
    [property: FromQuery(Name = "pageSize")] int PageSize = 20);
