namespace Shop.Modules.Products.Contracts;

/// <summary>Whether every requested line was reserved; false means no lines were reserved.</summary>
/// <param name="Success">True when every line was reserved.</param>
/// <param name="Shortages">
/// The products without enough stock when the reservation was refused. Empty when it succeeded, and also
/// when it was refused because the order had already been released.
/// </param>
public sealed record ReserveResult(bool Success, IReadOnlyList<StockShortage> Shortages)
{
    /// <summary>A result without shortages.</summary>
    public ReserveResult(bool success)
        : this(success, [])
    {
    }
}
