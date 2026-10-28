namespace Shop.Modules.Products.Images;

internal interface IImageStorage
{
    Task<string> SaveAsync(Stream content, string extension, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
    Task<StoredImage?> OpenReadAsync(string path, CancellationToken ct);
}

internal sealed record StoredImage(Stream Content, string ContentType);

internal sealed class ImageStorageOptions
{
    public string RootPath { get; set; } = "App_Data/images";
}
