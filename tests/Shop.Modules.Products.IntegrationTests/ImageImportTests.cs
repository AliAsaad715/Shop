using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Domain;
using Shop.Modules.Products.Images;
using Shop.Modules.Products.Seeding;
using Shop.SharedKernel.Persistence;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

[Collection("S3 images")]
public sealed class ImageImportTests(S3ProductFixture fixture)
{
    [Fact]
    public async Task ImportRetainsTheDatabaseKeyAndIsRepeatable()
    {
        using var source = new ImportSource();
        var key = await source.WriteAsync(S3TestImages.Bytes());
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
        var product = new Product { Name = "Legacy", Description = "Import", Price = 10, CategoryId = fixture.CategoryId, ImagePath = key };
        database.Products.Add(product);
        await database.SaveChangesAsync();
        var importer = Importer(scope.ServiceProvider, source.Root);
        var first = await importer.ImportAsync(default);
        Assert.Equal(1, first.Copied);
        Assert.Equal(0, first.Failed);
        var second = await importer.ImportAsync(default);
        Assert.Equal(0, second.Copied);
        Assert.Equal(1, second.Matched);
        Assert.Equal(0, second.Failed);
        Assert.Equal(key, (await database.Products.AsNoTracking().SingleAsync(item => item.Id == product.Id)).ImagePath);
        Assert.True(File.Exists(Path.Combine(source.Root, key)));
    }

    [Fact]
    public async Task ConflictingDestinationIsNotOverwritten()
    {
        using var source = new ImportSource();
        var key = await source.WriteAsync(S3TestImages.Bytes());
        var different = S3TestImages.Bytes().Concat(new byte[] { 42 }).ToArray();
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<S3ImageStorage>();
        await using var content = new MemoryStream(different);
        Assert.True(await storage.PutIfAbsentAsync(key, content, default));
        var result = await Importer(scope.ServiceProvider, source.Root).ImportAsync(default);
        Assert.Equal(1, result.Failed);
        var image = await storage.OpenReadAsync(key, default);
        Assert.NotNull(image);
        await using var stream = image.Content;
        using var actual = new MemoryStream();
        await stream.CopyToAsync(actual);
        Assert.Equal(different, actual.ToArray());
        // The conditional write is also used when another importer wins between the existence check and PUT.
        content.Position = 0;
        Assert.False(await storage.PutIfAbsentAsync(key, content, default));
    }

    [Theory]
    [InlineData("invalid-name.png", false)]
    [InlineData("valid-name.png", true)]
    public async Task InvalidNamesOrSignaturesAreReportedAndRetained(string name, bool generatedName)
    {
        using var source = new ImportSource();
        var key = await source.WriteAsync("not an image"u8.ToArray(), generatedName ? null : name);
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var result = await Importer(scope.ServiceProvider, source.Root).ImportAsync(default);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Copied);
        Assert.True(File.Exists(Path.Combine(source.Root, key)));
    }

    [Fact]
    public async Task MissingReferencedImageFailsEvenWhenTheSourceContainsOtherImages()
    {
        using var source = new ImportSource();
        await source.WriteAsync(S3TestImages.Bytes());
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
        var product = new Product { Name = "Missing legacy", Description = "Import", Price = 10, CategoryId = fixture.CategoryId, ImagePath = ImageKey.Create(".png") };
        database.Products.Add(product);
        await database.SaveChangesAsync();
        try
        {
            var result = await Importer(scope.ServiceProvider, source.Root).ImportAsync(default);
            Assert.Equal(1, result.Copied);
            Assert.Equal(1, result.Failed);
        }
        finally { database.Products.Remove(product); await database.SaveChangesAsync(); }
    }

    [Fact]
    public async Task OversizedSourceIsNeverUploaded()
    {
        using var source = new ImportSource();
        var key = await source.WriteAsync(new byte[ImageUploadLimits.ImageBytes + 1]);
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var result = await Importer(scope.ServiceProvider, source.Root).ImportAsync(default);
        Assert.Equal(1, result.Failed);
        Assert.Null(await scope.ServiceProvider.GetRequiredService<S3ImageStorage>().OpenReadAsync(key, default));
    }

    [Fact]
    public async Task ProductsDirectoryCannotBeALinkOutsideTheSource()
    {
        using var source = new ImportSource();
        using var outside = new ImportSource();
        await outside.WriteAsync(S3TestImages.Bytes());
        Directory.Delete(source.Products); // Empty directory created by this exact fixture.
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", source.Products, outside.Products }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(source.Products, outside.Products);
        await using var scope = fixture.App.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidDataException>(() => Importer(scope.ServiceProvider, source.Root).ImportAsync(default));
    }

    [Fact]
    public async Task DemoSeedingUsesS3AndCanBeRepeatedWithoutChangingImages()
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var seeder = new DemoProductSeeder(fixture.App.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DemoSeedOptions { Enabled = true }), NullLogger<DemoProductSeeder>.Instance);
        await seeder.SeedAsync(default);
        var database = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
        var ids = DemoProducts.All.Select(product => product.Id).ToArray();
        var first = await database.Products.AsNoTracking().Where(product => ids.Contains(product.Id)).OrderBy(product => product.Id)
            .Select(product => product.ImagePath).ToListAsync();
        Assert.Equal(ids.Length, first.Count);
        var count = await fixture.ObjectCountAsync();
        foreach (var key in first)
        {
            var image = await scope.ServiceProvider.GetRequiredService<IImageStorage>().OpenReadAsync(key, default);
            Assert.NotNull(image);
            await image.Content.DisposeAsync();
        }
        await seeder.SeedAsync(default);
        var second = await database.Products.AsNoTracking().Where(product => ids.Contains(product.Id)).OrderBy(product => product.Id)
            .Select(product => product.ImagePath).ToListAsync();
        Assert.Equal(first, second);
        Assert.Equal(count, await fixture.ObjectCountAsync());
    }

    private static ImageImporter Importer(IServiceProvider services, string source) => new(
        services.GetRequiredService<S3ImageStorage>(), services.GetRequiredService<ProductsDbContext>(),
        Options.Create(new ImageStorageOptions { Provider = "S3", RootPath = source }),
        services.GetRequiredService<IHostEnvironment>(), NullLogger<ImageImporter>.Instance);

    private sealed class ImportSource : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("shop-image-import-");
        public string Root => _directory.FullName;
        public string Products => Path.Combine(Root, "products");
        public ImportSource() => Directory.CreateDirectory(Products);
        public async Task<string> WriteAsync(byte[] bytes, string? name = null)
        {
            var key = "products/" + (name ?? $"{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(Path.Combine(Root, key), bytes);
            return key;
        }
        public void Dispose()
        {
            if (!_directory.FullName.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !_directory.Name.StartsWith("shop-image-import-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup path.");
            // Remove a link itself before recursively cleaning the known temporary source; never traverse its target.
            if (Directory.Exists(Products) && (File.GetAttributes(Products) & FileAttributes.ReparsePoint) != 0) Directory.Delete(Products);
            _directory.Delete(recursive: true);
        }
    }
}
