namespace Shop.Modules.Products.Contracts;

/// <summary>The warehouse is down or unreachable, even after the client's retries.</summary>
public sealed class InventoryUnavailableException(string message, Exception? innerException = null)
    : InventoryException(message, innerException);
