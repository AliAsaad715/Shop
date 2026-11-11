using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Catalog;
using Shop.Modules.Products.Domain;
using Shop.Modules.Products.Images;

namespace Shop.Modules.Products.Seeding;

// Adds the demo catalog when DemoSeed:Enabled is true. Runs when the host starts, after Program.cs
// has migrated the database. Products that already exist are left alone, so a restart never undoes
// an admin's edit, and each new product gets its own copy of the placeholder image.
internal sealed class DemoProductSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<DemoSeedOptions> options,
    ILogger<DemoProductSeeder> logger) : IHostedService
{
    internal const string ImageResource = "Shop.Modules.Products.Seeding.demo-product.png";

    // Fixed and a minute apart, so the catalog lists the demo products in the same order every time
    private static readonly DateTimeOffset SeededAt = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
        var images = scope.ServiceProvider.GetRequiredService<IImageStorage>();

        var ids = DemoProducts.All.Select(product => product.Id).ToArray();
        var existing = await context.Products.Where(product => ids.Contains(product.Id))
            .Select(product => product.Id).ToListAsync(cancellationToken);
        var missing = DemoProducts.All.Index().Where(item => !existing.Contains(item.Item.Id)).ToList();
        if (missing.Count == 0)
        {
            logger.LogInformation("Demo catalog already present");
            return;
        }

        var saved = new List<string>();
        try
        {
            foreach (var (index, demo) in missing)
            {
                await using var image = OpenImage();
                var path = await images.SaveAsync(image, ".png", cancellationToken);
                saved.Add(path);
                context.Products.Add(new Product
                {
                    Id = demo.Id,
                    Name = demo.Name,
                    Description = demo.Description,
                    Price = demo.Price,
                    CategoryId = demo.CategoryId,
                    ImagePath = path,
                    CreatedAt = SeededAt.AddMinutes(index),
                });
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // No images left behind for products that were never saved
            foreach (var path in saved)
            {
                try
                {
                    await images.DeleteAsync(path, CancellationToken.None);
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(cleanupError, "Could not remove demo image {ImagePath}", path);
                }
            }

            throw;
        }

        await scope.ServiceProvider.GetRequiredService<CatalogCacheInvalidator>()
            .ProductsCreatedAsync(missing.Select(item => (item.Item.Id, item.Item.CategoryId)));
        logger.LogInformation("Seeded {Count} demo products", missing.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static Stream OpenImage() =>
        typeof(DemoProductSeeder).Assembly.GetManifestResourceStream(ImageResource)
        ?? throw new InvalidOperationException($"The {ImageResource} resource is missing from the assembly.");
}
