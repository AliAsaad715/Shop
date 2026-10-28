using Shop.Modules.Products.Contracts;

namespace Shop.Modules.Products.Inventory;

// Requirement 2 replaces this stateless placeholder with a gRPC client.
internal sealed class PlaceholderInventoryService : IInventoryService
{
    public Task<int> GetAvailableAsync(Guid productId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // No stock is tracked in Requirement 1; this is a sentinel, not a measured count.
        return Task.FromResult(int.MaxValue);
    }

    public Task<ReserveResult> ReserveAsync(Guid orderId, IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lines);

        return Task.FromResult(new ReserveResult(true));
    }

    public Task ReleaseAsync(Guid orderId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.CompletedTask;
    }
}
