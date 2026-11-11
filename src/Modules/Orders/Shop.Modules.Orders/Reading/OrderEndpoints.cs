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
using Shop.SharedKernel.Persistence;

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
        [AsParameters] ListOrdersRequest request, ModuleReadDbContextFactory<OrdersDbContext> contexts,
        DatabaseReadRouter routing, ICurrentUser currentUser,
        HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "no-store";
        var isAdmin = currentUser.IsInRole(Roles.Admin);
        if (!isAdmin && currentUser.UserId is null)
        {
            return Problems.Create(StatusCodes.Status401Unauthorized, ErrorCodes.Common.Unauthorized, "Unauthorized");
        }
        var page = await contexts.ReadAsync(await routing.RouteAsync(ct), async (context, token) =>
        {
            var orders = context.Orders.AsNoTracking();
            // Ownership always comes from the token, on either database and on a fallback retry.
            if (!isAdmin) orders = orders.Where(order => order.CustomerId == currentUser.UserId);
            if (request.Status is { } status) orders = orders.Where(order => order.Status == status);
            var totalCount = await orders.CountAsync(token);
            var items = await orders.OrderByDescending(order => order.CreatedAt).ThenByDescending(order => order.Id)
                .Skip((int)(((long)request.Page - 1) * request.PageSize)).Take(request.PageSize)
                .Select(order => new OrderSummaryResponse(
                    order.Id, order.CustomerId, order.Status, order.Total,
                    order.Lines.Sum(line => line.Quantity), order.CreatedAt, order.UpdatedAt))
                .ToListAsync(token);
            return new PagedResponse<OrderSummaryResponse>(items, request.Page, request.PageSize, totalCount);
        }, ct);
        return TypedResults.Ok(page);
    }

    public static async Task<Results<Ok<OrderResponse>, ProblemHttpResult>> GetAsync(
        Guid id, ModuleReadDbContextFactory<OrdersDbContext> contexts, DatabaseReadRouter routing,
        ICurrentUser currentUser, ILoggerFactory loggers,
        HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "no-store";

        // One round trip for the order, its lines and its history, projected straight into the response. Joining two
        // collections multiplies rows, but an order's history never passes 4 (Pending, Confirmed, Shipped, Delivered),
        // so the query returns at most 4 rows per line: far cheaper than the two extra round trips a split query takes
        var order = await contexts.ReadAsync(await routing.RouteAsync(ct), (context, token) => context.Orders.AsSingleQuery()
            .Where(order => order.Id == id)
            .Select(OrderResponse.Projection)
            .SingleOrDefaultAsync(token), ct);

        return order is not null
            && OrderAccess.CanView(order.Id, order.CustomerId, currentUser, loggers.CreateLogger(OrderAccess.SecurityLogCategory))
            ? TypedResults.Ok(order)
            : NotFound();
    }

    public static ProblemHttpResult NotFound() =>
        Problems.Create(StatusCodes.Status404NotFound, ErrorCodes.Orders.NotFound, "Order not found");
}
