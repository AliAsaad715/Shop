namespace Shop.Modules.Products.Images;

internal static class ImageContent
{
    // A single bounded buffer, including one byte to detect excess content. Never spool images onto an API disk.
    public static async Task<MemoryStream> ReadAsync(Stream source, CancellationToken ct)
    {
        var bytes = new byte[ImageUploadLimits.ImageBytes + 1];
        var count = await source.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, ct);
        if (count > ImageUploadLimits.ImageBytes) throw new ImageUploadTooLargeException();
        return new MemoryStream(bytes, 0, count, writable: false, publiclyVisible: true);
    }
}
