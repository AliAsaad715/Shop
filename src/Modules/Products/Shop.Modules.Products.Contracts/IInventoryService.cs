namespace Shop.Modules.Products.Contracts;

/// <summary>
/// The inventory boundary used by other modules. Each operation maps to a future warehouse RPC.
/// </summary>
public interface IInventoryService
{
    /// <summary>
    /// Returns available units. Requirement 1 returns <see cref="int.MaxValue"/> to represent
    /// unlimited availability until the warehouse service supplies real stock counts.
    /// </summary>
    Task<int> GetAvailableAsync(Guid productId, CancellationToken ct);

    /// <summary>
    /// Reserves all lines or none. Repeating a reservation for the same order must not consume stock twice.
    /// Requirement 1 always returns a successful reservation without changing stock.
    /// </summary>
    Task<ReserveResult> ReserveAsync(Guid orderId, IReadOnlyList<StockLine> lines, CancellationToken ct);

    /// <summary>
    /// Releases an order's reservation. Safe to repeat, including when no reservation exists.
    /// </summary>
    Task ReleaseAsync(Guid orderId, CancellationToken ct);
}
