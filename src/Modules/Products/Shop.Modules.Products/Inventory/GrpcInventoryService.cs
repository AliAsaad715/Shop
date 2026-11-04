using Grpc.Core;
using Inventory.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Modules.Products.Contracts;

namespace Shop.Modules.Products.Inventory;

/// <summary>
/// <see cref="IInventoryService"/> over gRPC to the warehouse. Each call carries its own deadline, the
/// channel retries UNAVAILABLE inside it, and every gRPC failure leaves as an <see cref="InventoryException"/>.
/// </summary>
internal sealed class GrpcInventoryService(
    InventoryService.InventoryServiceClient client,
    IOptions<InventoryOptions> options,
    ILogger<GrpcInventoryService> logger) : IInventoryService
{
    private readonly InventoryOptions _options = options.Value;

    public async Task<int> GetAvailableAsync(Guid productId, CancellationToken ct)
    {
        var request = new GetStockRequest { ProductId = productId.ToString() };
        var response = await CallAsync(
            "GetStock", productId, call => client.GetStockAsync(request, call), _options.GetStockTimeout, ct);

        return response.Available;
    }

    public async Task<ReserveResult> ReserveAsync(Guid orderId, IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var request = new ReserveRequest { OrderId = orderId.ToString() };
        request.Items.AddRange(lines.Select(line => new ReserveItem
        {
            ProductId = line.ProductId.ToString(),
            Quantity = line.Quantity,
        }));

        var response = await CallAsync(
            "Reserve", orderId, call => client.ReserveAsync(request, call), _options.ReserveTimeout, ct);
        if (response.Reserved)
        {
            logger.LogInformation("Reserved stock for order {OrderId}", orderId);
            return new ReserveResult(true);
        }

        var shortages = response.Shortages.Select(ToShortage).ToList();
        logger.LogInformation("The warehouse refused order {OrderId}; {ShortageCount} products are short", orderId, shortages.Count);
        return new ReserveResult(false, shortages);
    }

    public async Task ReleaseAsync(Guid orderId, CancellationToken ct)
    {
        var request = new ReleaseRequest { OrderId = orderId.ToString() };
        await CallAsync("Release", orderId, call => client.ReleaseAsync(request, call), _options.ReleaseTimeout, ct);

        logger.LogInformation("Released the reservation for order {OrderId}", orderId);
    }

    private async Task<TResponse> CallAsync<TResponse>(
        string method, Guid id, Func<CallOptions, AsyncUnaryCall<TResponse>> call, TimeSpan timeout, CancellationToken ct)
    {
        // The deadline bounds the whole call, retries included, and travels to the warehouse
        var deadline = DateTime.UtcNow.Add(timeout);
        try
        {
            return await call(new CallOptions(deadline: deadline, cancellationToken: ct));
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException($"The caller cancelled {method} for {id}.", error, ct);
        }
        catch (RpcException error)
        {
            logger.LogWarning(error, "Warehouse {Method} for {Id} failed with {StatusCode}", method, id, error.StatusCode);

            // Past the deadline the outcome is unknown whatever the status says: the warehouse's own deadline can
            // fire first and report its cancelled work as an error of its own
            if (error.StatusCode == StatusCode.DeadlineExceeded || DateTime.UtcNow >= deadline)
            {
                throw new InventoryTimeoutException(
                    $"The warehouse didn't answer {method} for {id} within {timeout.TotalMilliseconds:0} ms.", error);
            }

            throw error.StatusCode == StatusCode.Unavailable
                ? new InventoryUnavailableException($"The warehouse is unavailable, so {method} for {id} failed.", error)
                : new InventoryException($"The warehouse failed {method} for {id} with {error.StatusCode}.", error);
        }
    }

    private static StockShortage ToShortage(Shortage shortage) =>
        Guid.TryParse(shortage.ProductId, out var productId)
            ? new StockShortage(productId, shortage.Requested, shortage.Available)
            : throw new InventoryException($"The warehouse reported a shortage for '{shortage.ProductId}', which isn't a product ID.");
}
