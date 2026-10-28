using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Domain;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

[Collection("Products database")]
public sealed class ProductsSchemaTests
{
    [Fact]
    public async Task MigrationSupportsCatalogQueriesConstraintsAndRollback()
    {
        // An explicit connection must point to a disposable database: this test rolls back its schema.
        var connectionString = Environment.GetEnvironmentVariable("SHOP_PRODUCTS_TEST_CONNECTION");
        await using var postgres = string.IsNullOrWhiteSpace(connectionString)
            ? new PostgreSqlBuilder("postgres:18").Build()
            : null;
        if (postgres is not null)
        {
            await postgres.StartAsync();
            connectionString = postgres.GetConnectionString();
        }

        var options = new DbContextOptionsBuilder<ProductsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ProductsDbContext.Schema))
            .Options;
        await using var context = new ProductsDbContext(options);

        Assert.False(context.Database.HasPendingModelChanges());
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("0");
        await context.Database.MigrateAsync();
        var categories = await context.Categories.OrderBy(category => category.Name).ToListAsync();
        Assert.Equal(new[] { "Books", "Clothing", "Electronics", "Home" }, categories.Select(category => category.Name));
        var originalCategoryIds = categories.Select(category => category.Id).ToArray();

        // Equal timestamps exercise the ID tie-breaker across page boundaries.
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = NewProduct(categories[0].Id, createdAt);
        var second = NewProduct(categories[0].Id, createdAt);
        var newest = NewProduct(categories[0].Id, createdAt.AddMinutes(1));
        var otherCategory = NewProduct(categories[1].Id, createdAt.AddMinutes(2));
        context.Products.AddRange(first, second, newest, otherCategory);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var catalog = context.Products.Where(product => product.CategoryId == categories[0].Id)
            .OrderByDescending(product => product.CreatedAt).ThenBy(product => product.Id);
        var firstPage = await catalog.Take(2).ToListAsync();
        var secondPage = await catalog.Skip(2).Take(2).ToListAsync();
        var tiedIds = new[] { first.Id, second.Id }.Order().ToArray();
        Assert.Equal(new[] { newest.Id, tiedIds[0], tiedIds[1] }, firstPage.Concat(secondPage).Select(product => product.Id));
        Assert.All(firstPage.Concat(secondPage), product => Assert.Equal(19.95m, product.Price));
        Assert.All(firstPage.Concat(secondPage), product => Assert.Equal(TimeSpan.Zero, product.CreatedAt.Offset));

        await AssertRejectedAsync(options, NewProduct(Guid.CreateVersion7(), createdAt), PostgresErrorCodes.ForeignKeyViolation);
        var negativePrice = NewProduct(categories[0].Id, createdAt);
        negativePrice.Price = -0.01m;
        await AssertRejectedAsync(options, negativePrice, PostgresErrorCodes.CheckViolation);

        // A category in use cannot disappear underneath a product.
        await using (var deletion = new ProductsDbContext(options))
        {
            deletion.Categories.Remove(new Category { Id = categories[0].Id, Name = categories[0].Name });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => deletion.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.RestrictViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }

        // Re-running startup migrations does not duplicate seed data.
        await context.Database.MigrateAsync();
        Assert.Equal(4, await context.Categories.CountAsync());

        await migrator.MigrateAsync("0");
        var remainingTables = await context.Database.SqlQueryRaw<string>(
            "SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = 'products' AND tablename IN ('products', 'categories')")
            .ToListAsync();
        Assert.Empty(remainingTables);

        await context.Database.MigrateAsync();
        Assert.Empty(await context.Products.ToListAsync());
        Assert.Equal(originalCategoryIds, await context.Categories.OrderBy(category => category.Name).Select(category => category.Id).ToArrayAsync());
    }

    private static Product NewProduct(Guid categoryId, DateTimeOffset createdAt) => new()
    {
        Name = "Test product",
        Description = "A product for the schema integration test.",
        Price = 19.95m,
        ImagePath = "products/test-product.png",
        CategoryId = categoryId,
        CreatedAt = createdAt,
    };

    private static async Task AssertRejectedAsync(DbContextOptions<ProductsDbContext> options, Product product, string sqlState)
    {
        await using var context = new ProductsDbContext(options);
        context.Products.Add(product);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
}
