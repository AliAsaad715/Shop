using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shop.Modules.Orders.Data;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Paging;

namespace Shop.Modules.Orders.Reading;

internal static class OrderEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/orders", ListAsync)
            .WithName("ListOrders")
            .WithTags("Orders")
            .WithSummary("List orders newest first: a customer sees their own, an admin sees all (pageSize 1–50, default 20)")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        endpoints.MapGet("/orders/{id}", GetAsync)
            .WithName("GetOrder")
            .WithTags("Orders")
            .WithSummary("Get an order with its lines and status history; someone else's order is 404")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    public static async Task<Results<Ok<PagedResponse<OrderSummaryResponse>>, ProblemHttpResult>> ListAsync(
        [AsParameters] ListOrdersRequest request, OrdersDbContext context, ICurrentUser currentUser,
        HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "no-store";
        var orders = context.Orders.AsNoTracking();
        if (!currentUser.IsInRole(Roles.Admin))
        {
            // The filter comes from the token only; there is no customer parameter to tamper with
            if (currentUser.UserId is not { } customerId)
            {
                return Problems.Create(StatusCodes.Status401Unauthorized, ErrorCodes.Common.Unauthorized, "Unauthorized");
            }

            orders = orders.Where(order => order.CustomerId == customerId);
        }

        if (request.Status is { } status)
        {
            orders = orders.Where(order => order.Status == status);
        }

        var totalCount = await orders.CountAsync(ct);
        var items = await orders.OrderByDescending(order => order.CreatedAt).ThenByDescending(order => order.Id)
            .Skip((int)(((long)request.Page - 1) * request.PageSize)).Take(request.PageSize)
            .Select(order => new OrderSummaryResponse(
                order.Id, order.CustomerId, order.Status, order.Total,
                order.Lines.Sum(line => line.Quantity), order.CreatedAt, order.UpdatedAt))
            .ToListAsync(ct);

        return TypedResults.Ok(new PagedResponse<OrderSummaryResponse>(items, request.Page, request.PageSize, totalCount));
    }

    public static async Task<Results<Ok<OrderResponse>, ProblemHttpResult>> GetAsync(
        Guid id, OrdersDbContext context, ICurrentUser currentUser, ILoggerFactory loggers,
        HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "no-store";
        var order = await context.Orders.AsNoTracking().AsSplitQuery()
            .Include(order => order.Lines)
            .Include(order => order.History)
            .SingleOrDefaultAsync(order => order.Id == id, ct);

        return order is not null && OrderAccess.CanView(order, currentUser, loggers.CreateLogger(OrderAccess.SecurityLogCategory))
            ? TypedResults.Ok(OrderResponse.From(order))
            : NotFound();
    }

    public static ProblemHttpResult NotFound() =>
        Problems.Create(StatusCodes.Status404NotFound, ErrorCodes.Orders.NotFound, "Order not found");
}
