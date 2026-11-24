namespace Shop.Modules.Products.Images;

// IOException keeps the existing compensating-delete handlers applicable to both storage providers.
internal sealed class ImageStorageUnavailableException(Exception innerException)
    : IOException("Product image storage is unavailable.", innerException);
