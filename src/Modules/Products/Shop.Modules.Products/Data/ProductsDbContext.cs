using Microsoft.EntityFrameworkCore;

namespace Shop.Modules.Products.Data;

internal sealed class ProductsDbContext(DbContextOptions<ProductsDbContext> options) : DbContext(options)
{
    internal const string Schema = "products";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProductsDbContext).Assembly);
    }
}
