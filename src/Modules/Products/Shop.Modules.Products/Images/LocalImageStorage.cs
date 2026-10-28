using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shop.Modules.Products.Images;

internal sealed class LocalImageStorage(
    IOptions<ImageStorageOptions> options, IHostEnvironment environment, ILogger<LocalImageStorage> logger) : IImageStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath);

    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct)
    {
        if (ImageSignatures.ContentType(extension) is null)
        {
            throw new ArgumentException("Unsupported image extension.", nameof(extension));
        }

        ct.ThrowIfCancellationRequested();
        var path = $"products/{Guid.CreateVersion7():N}{extension}";
        var fullPath = Resolve(path)!;
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var created = false;
        try
        {
            await using var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            created = true;
            var buffer = new byte[81920];
            var total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > ImageUploadLimits.ImageBytes)
                {
                    throw new ImageUploadTooLargeException();
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            return path;
        }
        catch
        {
            try
            {
                if (created)
                {
                    File.Delete(fullPath);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(error, "Could not remove incomplete image {ImagePath}", path);
            }

            throw;
        }
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = Resolve(path) ?? throw new ArgumentException("Invalid stored image path.", nameof(path));
        File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<StoredImage?> OpenReadAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = Resolve(path);
        if (fullPath is null)
        {
            return Task.FromResult<StoredImage?>(null);
        }

        try
        {
            var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult<StoredImage?>(new StoredImage(stream, ImageSignatures.ContentType(Path.GetExtension(path))!));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<StoredImage?>(null);
        }
    }

    private string? Resolve(string path)
    {
        if (!path.StartsWith("products/", StringComparison.Ordinal))
        {
            return null;
        }

        var fileName = path["products/".Length..];
        var extension = Path.GetExtension(fileName);
        return ImageSignatures.ContentType(extension) is not null && Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out _)
            && fileName == Path.GetFileName(fileName)
            ? Path.Combine(_root, "products", fileName)
            : null;
    }
}
