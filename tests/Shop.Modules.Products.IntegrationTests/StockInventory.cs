using System.Collections.Concurrent;
using Shop.Modules.Products.Contracts;

namespace Shop.Modules.Products.IntegrationTests;

// Answers stock lookups from a table, or fails as a warehouse that is down or slow would
public sealed class StockInventory : IInventoryService
{
    public ConcurrentDictionary<Guid, int> Stock { get; } = new();

    // Every product asked about, so tests can prove when the warehouse was not asked
    public ConcurrentQueue<Guid> Lookups { get; } = new();

    public Func<Exception>? Failure { get; set; }

    // A product the warehouse doesn't know counts as 0, as in the real warehouse
    public Task<int> GetAvailableAsync(Guid productId, CancellationToken ct)
    {
        Lookups.Enqueue(productId);
        return Failure is { } failure
            ? Task.FromException<int>(failure())
            : Task.FromResult(Stock.GetValueOrDefault(productId));
    }

    public Task<ReserveResult> ReserveAsync(Guid orderId, IReadOnlyList<StockLine> lines, CancellationToken ct) =>
        throw new NotSupportedException("The catalog never reserves stock.");

    public Task ReleaseAsync(Guid orderId, CancellationToken ct) =>
        throw new NotSupportedException("The catalog never releases stock.");
}
