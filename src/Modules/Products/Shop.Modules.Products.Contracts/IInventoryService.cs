namespace Shop.Modules.Products.Contracts;

/// <summary>
/// The inventory boundary used by other modules, answered by the warehouse service. When the
/// warehouse can't answer, every method throws an <see cref="InventoryException"/>: an
/// <see cref="InventoryUnavailableException"/> when it is down or unreachable, and an
/// <see cref="InventoryTimeoutException"/> when it misses the call's deadline.
/// </summary>
public interface IInventoryService
{
    /// <summary>
    /// Returns the units available. A product the warehouse doesn't know has 0.
    /// </summary>
    Task<int> GetAvailableAsync(Guid productId, CancellationToken ct);

    /// <summary>
    /// Reserves all lines or none. Repeating a reservation for the same order doesn't consume stock twice,
    /// and a reservation for an order that was already released is refused.
    /// </summary>
    Task<ReserveResult> ReserveAsync(Guid orderId, IReadOnlyList<StockLine> lines, CancellationToken ct);

    /// <summary>
    /// Releases an order's reservation. Safe to repeat, including when no reservation exists.
    /// </summary>
    Task ReleaseAsync(Guid orderId, CancellationToken ct);
}
