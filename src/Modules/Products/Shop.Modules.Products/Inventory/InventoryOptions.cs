namespace Shop.Modules.Products.Inventory;

/// <summary>
/// Where the warehouse is and how long each call may take, bound from the <c>Inventory</c> section.
/// </summary>
internal sealed class InventoryOptions
{
    public const string Section = "Inventory";

    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The warehouse's gRPC address; compose sets <c>http://inventory:50051</c>.</summary>
    public Uri? Address { get; init; }

    // One deadline per call, covering every retry; the plan's starting values, to tune against the real warehouse
    public TimeSpan GetStockTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ReserveTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReleaseTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public static bool HasValidAddress(InventoryOptions options) =>
        options.Address is { IsAbsoluteUri: true } address
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);

    public static bool HasValidTimeouts(InventoryOptions options) =>
        new[] { options.GetStockTimeout, options.ReserveTimeout, options.ReleaseTimeout }
            .All(timeout => timeout > TimeSpan.Zero && timeout <= MaxTimeout);
}
