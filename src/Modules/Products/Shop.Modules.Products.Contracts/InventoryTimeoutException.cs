namespace Shop.Modules.Products.Contracts;

/// <summary>
/// The warehouse didn't answer within the call's deadline. A reservation may still have been applied,
/// so a caller that gives up on a Reserve releases the order to be sure.
/// </summary>
public sealed class InventoryTimeoutException(string message, Exception? innerException = null)
    : InventoryException(message, innerException);
