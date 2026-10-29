using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Shop.Modules.Orders.Data;
using Shop.Modules.Orders.Domain;
using Shop.Modules.Orders.Reading;
using Shop.Modules.Products.Contracts;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;

namespace Shop.Modules.Orders.Placement;

internal static class CreateOrderEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/orders", HandleAsync)
            .WithName("CreateOrder")
            .WithTags("Orders")
            .WithSummary("Place an order; prices come from the catalog and the customer from the token")
            .RequireAuthorization(Policies.CustomerOnly)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Reserve the stock, save the order, and release the reservation if the save fails, so every
    /// exit other than 201 leaves the stock as it was. Requirement 2 swaps the inventory for a gRPC
    /// client without changing this flow.
    /// </summary>
    public static async Task<Results<Created<OrderResponse>, ProblemHttpResult>> HandleAsync(
        CreateOrderRequest request, OrdersDbContext context, ICurrentUser currentUser, IProductCatalog catalog,
        IInventoryService inventory, TimeProvider time, HttpContext httpContext, ILoggerFactory loggers, CancellationToken ct)
    {
        if (currentUser.UserId is not { } customerId)
        {
            return Problems.Create(StatusCodes.Status401Unauthorized, ErrorCodes.Common.Unauthorized, "Unauthorized");
        }

        var lines = request.Lines!;
        var products = await catalog.GetByIdsAsync(lines.Select(line => line!.ProductId!.Value).ToArray(), ct);
        var unknown = lines
            .Select((line, index) => (line, index))
            .Where(item => !products.ContainsKey(item.line!.ProductId!.Value))
            .ToDictionary(item => $"lines[{item.index}].productId", _ => new[] { "The product does not exist." });
        if (unknown.Count > 0)
        {
            return Problems.Validation(unknown);
        }

        var order = Order.Place(
            customerId,
            lines.Select(line =>
            {
                var product = products[line!.ProductId!.Value];
                return (product.Id, product.Name, product.Price, line.Quantity!.Value);
            }),
            time.GetUtcNow());

        var reservation = await inventory.ReserveAsync(
            order.Id, order.Lines.Select(line => new StockLine(line.ProductId, line.Quantity)).ToList(), ct);
        if (!reservation.Success)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ErrorCodes.Orders.InsufficientStock,
                "Insufficient stock", "Not enough stock is available for one or more products.");
        }

        try
        {
            context.Orders.Add(order);
            await context.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            // Not the request's token: a client that disconnects must still get its reservation released
            await ReleaseAfterFailedSaveAsync(inventory, order.Id, loggers.CreateLogger(typeof(CreateOrderEndpoint)));
            throw;
        }

        var location = $"{httpContext.Request.PathBase}{httpContext.Request.Path.ToString().TrimEnd('/')}/{order.Id}";
        return TypedResults.Created(location, OrderResponse.From(order));
    }

    private static async Task ReleaseAfterFailedSaveAsync(IInventoryService inventory, Guid orderId, ILogger logger)
    {
        try
        {
            await inventory.ReleaseAsync(orderId, CancellationToken.None);
        }
        catch (Exception releaseError)
        {
            // The save error is the one the client must see; release is idempotent and can be retried
            logger.LogError(releaseError, "Could not release the reservation for order {OrderId} after its save failed", orderId);
        }
    }
}
