using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Shop.Modules.Orders.Data;
using Shop.Modules.Orders.Domain;
using Shop.Modules.Orders.Reading;
using Shop.Modules.Orders.Releases;
using Shop.Modules.Products.Contracts;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;

namespace Shop.Modules.Orders.Placement;

internal static class CreateOrderEndpoint
{
    // How long a client waits before trying again while the warehouse is down
    private const int RetryAfterSeconds = 5;

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/orders", HandleAsync)
            .WithName("CreateOrder")
            .WithTags("Orders")
            .WithSummary("Place an order; prices come from the catalog and the customer from the token")
            .RequireAuthorization(Policies.CustomerOnly)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

    /// <summary>
    /// Reserve the stock, save the order, and give the reservation back whenever the order isn't saved,
    /// so every exit other than 201 leaves the stock as it was.
    /// </summary>
    public static async Task<Results<Created<OrderResponse>, ProblemHttpResult>> HandleAsync(
        CreateOrderRequest request, OrdersDbContext context, ICurrentUser currentUser, IProductCatalog catalog,
        IInventoryService inventory, ReservationReleaser releaser, TimeProvider time, HttpContext httpContext, CancellationToken ct)
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

        ReserveResult reservation;
        try
        {
            // Not the request's token: a reservation in flight runs to its own deadline, so a client that
            // disconnects never leaves its outcome unknown
            reservation = await inventory.ReserveAsync(
                order.Id, order.Lines.Select(line => new StockLine(line.ProductId, line.Quantity)).ToList(), CancellationToken.None);
        }
        catch (InventoryException error)
        {
            // Down, slow or failing, the warehouse may still have applied the reservation (an answer lost on
            // the way back, or one that came after the deadline), so it goes back before the client hears
            await releaser.ReleaseAsync(order.Id);

            if (error is InventoryUnavailableException)
            {
                httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return Problems.Create(StatusCodes.Status503ServiceUnavailable, ErrorCodes.Orders.WarehouseUnavailable,
                    "Warehouse unavailable",
                    $"The warehouse can't be reached, so no order was placed. Try again in {RetryAfterSeconds} seconds.");
            }

            if (error is InventoryTimeoutException)
            {
                return Problems.Create(StatusCodes.Status504GatewayTimeout, ErrorCodes.Orders.WarehouseTimeout,
                    "Warehouse timed out", "The warehouse didn't answer in time, so no order was placed. Try again.");
            }

            throw;
        }

        if (!reservation.Success)
        {
            // Nothing was reserved, so nothing goes back
            var refused = Problems.Create(StatusCodes.Status409Conflict, ErrorCodes.Orders.InsufficientStock,
                "Insufficient stock", "Not enough stock is available for one or more products.");
            refused.ProblemDetails.Extensions["shortages"] = reservation.Shortages.Select(ShortageResponse.From).ToArray();
            return refused;
        }

        try
        {
            context.Orders.Add(order);
            await context.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            await releaser.ReleaseAsync(order.Id);
            throw;
        }

        var location = $"{httpContext.Request.PathBase}{httpContext.Request.Path.ToString().TrimEnd('/')}/{order.Id}";
        return TypedResults.Created(location, OrderResponse.From(order));
    }
}
