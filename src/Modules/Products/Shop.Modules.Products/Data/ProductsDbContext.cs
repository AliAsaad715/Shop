using Microsoft.EntityFrameworkCore;
using Shop.Modules.Products.Domain;

namespace Shop.Modules.Products.Data;

internal sealed class ProductsDbContext(DbContextOptions<ProductsDbContext> options) : DbContext(options)
{
    internal const string Schema = "products";

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProductsDbContext).Assembly);
    }
}
