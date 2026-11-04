namespace Shop.Modules.Products.Contracts;

/// <summary>
/// The warehouse failed an inventory call. The subtypes say why; any other failure, such as a request
/// the warehouse rejected, is this type itself. Callers never see the transport's own exceptions.
/// </summary>
public class InventoryException(string message, Exception? innerException = null)
    : Exception(message, innerException);
