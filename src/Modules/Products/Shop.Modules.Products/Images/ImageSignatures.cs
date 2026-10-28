namespace Shop.Modules.Products.Images;

internal static class ImageSignatures
{
    public static string? Extension(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return ".png";
        }

        if (header.StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
        {
            return ".jpg";
        }

        return header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)
            ? ".webp"
            : null;
    }

    public static string? ContentType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => null,
    };
}
