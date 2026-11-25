using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Modules.Products.Data;

namespace Shop.Modules.Products.Images;

internal sealed class ImageImporter(
    S3ImageStorage destination, ProductsDbContext database, IOptions<ImageStorageOptions> options,
    IHostEnvironment environment, ILogger<ImageImporter> logger)
{
    public async Task<ImageImportResult> ImportAsync(CancellationToken ct)
    {
        if (!options.Value.IsS3) throw new InvalidOperationException("Image import requires Images:Provider=S3.");
        if (string.IsNullOrWhiteSpace(options.Value.RootPath)) throw new InvalidOperationException("Set Images:RootPath to the legacy image directory.");
        var root = new DirectoryInfo(Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath));
        var products = new DirectoryInfo(Path.Combine(root.FullName, "products"));
        if (!products.Exists) throw new DirectoryNotFoundException("The legacy image directory must contain a products folder.");
        for (DirectoryInfo? directory = products; directory is not null; directory = directory.Parent)
            RejectLink(directory);
        await destination.EnsureAvailableAsync(ct);

        // Read the module's own schema only. A wrong/empty source must not hide missing referenced images.
        var referenced = await database.Products.AsNoTracking().Select(product => product.ImagePath).Distinct().ToListAsync(ct);
        var copied = 0;
        var matched = 0;
        var failed = 0;
        foreach (var file in products.EnumerateFileSystemInfos().OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var key = "products/" + file.Name;
            try
            {
                RejectLink(file);
                if (file is not FileInfo || !ImageKey.IsValid(key))
                    throw new InvalidDataException("The source contains a file or directory outside the generated image-key format.");
                await using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var content = await ImageContent.ReadAsync(source, ct);
                if (ImageSignatures.Extension(content.GetBuffer().AsSpan(0, (int)Math.Min(content.Length, 12))) != Path.GetExtension(key))
                    throw new InvalidDataException("The source image signature does not match its extension.");
                var expectedHash = await SHA256.HashDataAsync(content, ct);
                content.Position = 0;
                var existing = await destination.OpenReadAsync(key, ct);
                if (existing is not null)
                {
                    await VerifyAsync(existing, expectedHash, ct);
                    matched++;
                    continue;
                }
                var created = await destination.PutIfAbsentAsync(key, content, ct);
                var uploaded = await destination.OpenReadAsync(key, ct)
                    ?? throw new IOException("The uploaded image could not be read back.");
                await VerifyAsync(uploaded, expectedHash, ct);
                if (created) copied++; else matched++;
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                logger.LogError(error, "Image import failed for {ImagePath}; source retained", key);
            }
        }
        foreach (var key in referenced)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var image = await destination.OpenReadAsync(key, ct)
                    ?? throw new IOException("A product references a missing or invalid image key.");
                await using var stream = image.Content;
                // Actually read the image: successful headers alone do not prove that its data volumes are reachable.
                await using var content = await ImageContent.ReadAsync(stream, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                logger.LogError(error, "Product references unavailable image {ImagePath}", key);
            }
        }
        logger.LogInformation("Image import completed: {Copied} copied, {Matched} matched, {Failed} failed, {Referenced} referenced images checked",
            copied, matched, failed, referenced.Count);
        return new ImageImportResult(copied, matched, failed, referenced.Count);
    }

    private static async Task VerifyAsync(StoredImage image, byte[] expectedHash, CancellationToken ct)
    {
        await using var stream = image.Content;
        await using var content = await ImageContent.ReadAsync(stream, ct);
        var actual = await SHA256.HashDataAsync(content, ct);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actual))
            throw new InvalidDataException("The destination content differs; the existing object was not overwritten.");
    }

    private static void RejectLink(FileSystemInfo path)
    {
        if ((path.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Symbolic links and reparse points are not allowed in the import source.");
    }
}

internal sealed record ImageImportResult(int Copied, int Matched, int Failed, int Referenced);
