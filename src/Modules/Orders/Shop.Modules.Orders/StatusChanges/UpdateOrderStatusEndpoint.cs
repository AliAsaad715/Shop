using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shop.Modules.Orders.Data;
using Shop.Modules.Orders.Domain;
using Shop.Modules.Orders.Reading;
using Shop.Modules.Products.Contracts;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;

namespace Shop.Modules.Orders.StatusChanges;

internal static class UpdateOrderStatusEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPatch("/orders/{id}/status", HandleAsync)
            .WithName("UpdateOrderStatus")
            .WithTags("Orders")
            .WithSummary("Move an order to its next status (Pending → Confirmed → Shipped → Delivered, or Pending/Confirmed → Cancelled)")
            .RequireAuthorization(Policies.AdminOnly)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Every status change goes through here, which makes it the place Requirement 4 raises its
    /// event. Two admins changing the same order at once: the second save fails on the row version
    /// and the error contract answers 409.
    /// </summary>
    public static async Task<Results<Ok<OrderResponse>, ProblemHttpResult>> HandleAsync(
        Guid id, UpdateOrderStatusRequest request, OrdersDbContext context, ICurrentUser currentUser,
        IInventoryService inventory, TimeProvider time, ILoggerFactory loggers, HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "no-store";
        var order = await context.Orders.AsSplitQuery()
            .Include(order => order.Lines)
            .Include(order => order.History)
            .SingleOrDefaultAsync(order => order.Id == id, ct);
        if (order is null)
        {
            return OrderEndpoints.NotFound();
        }

        var next = request.Status!.Value;
        if (!order.CanMoveTo(next))
        {
            return Problems.Create(StatusCodes.Status409Conflict, ErrorCodes.Orders.InvalidStatusTransition,
                "Invalid status transition", $"An order in status {order.Status} can't move to {next}.");
        }

        order.MoveTo(next, currentUser.UserId!.Value, time.GetUtcNow());
        await context.SaveChangesAsync(ct);

        if (next == OrderStatus.Cancelled)
        {
            // After the save, so a cancellation that failed to commit never gives its stock back
            await ReleaseAsync(inventory, order.Id, loggers.CreateLogger(typeof(UpdateOrderStatusEndpoint)));
        }

        return TypedResults.Ok(OrderResponse.From(order));
    }

    private static async Task ReleaseAsync(IInventoryService inventory, Guid orderId, ILogger logger)
    {
        try
        {
            await inventory.ReleaseAsync(orderId, CancellationToken.None);
        }
        catch (Exception error)
        {
            // The order is already cancelled; release is idempotent, so a retry can return the stock later
            logger.LogError(error, "Order {OrderId} was cancelled but its reservation could not be released", orderId);
        }
    }
}
