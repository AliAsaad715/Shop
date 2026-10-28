namespace Shop.Modules.Products.Images;

internal static class ImageUploadLimits
{
    public const int ImageBytes = 2 * 1024 * 1024;
    public const int RequestBytes = ImageBytes + 64 * 1024;
}

internal sealed class ImageUploadTooLargeException : Exception;

internal sealed record ProductUploadMetadata;
