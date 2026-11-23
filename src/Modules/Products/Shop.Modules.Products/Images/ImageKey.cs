namespace Shop.Modules.Products.Images;

internal static class ImageKey
{
    public static string Create(string extension) => $"products/{Guid.CreateVersion7():N}{extension}";

    public static bool IsValid(string path)
    {
        if (!path.StartsWith("products/", StringComparison.Ordinal)) return false;
        var name = path["products/".Length..];
        var extension = Path.GetExtension(name);
        // Explicit separators matter even on Linux, where a backslash is a legal filename character.
        return !name.Contains('/') && !name.Contains('\\')
            && ImageSignatures.ContentType(extension) is not null
            && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _);
    }
}
