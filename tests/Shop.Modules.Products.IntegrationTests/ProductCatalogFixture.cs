using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Domain;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Validation;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

public sealed class ProductCatalogFixture : IAsyncLifetime
{
    public static readonly Guid FirstBookId = Guid.Parse("019a2180-1000-7000-8000-000000000001");
    public static readonly Guid SecondBookId = Guid.Parse("019a2180-1000-7000-8000-000000000002");
    public static readonly Guid NewestBookId = Guid.Parse("019a2180-1000-7000-8000-000000000003");
    public static readonly Guid HomeProductId = Guid.Parse("019a2180-1000-7000-8000-000000000004");

    private PostgreSqlContainer? _postgres;

    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid BooksCategoryId { get; private set; }

    public async Task InitializeAsync()
    {
        // Like the schema test, this fixture must use a disposable database: existing products are removed.
        var connectionString = Environment.GetEnvironmentVariable("SHOP_PRODUCTS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _postgres = new PostgreSqlBuilder("postgres:18").Build();
            await _postgres.StartAsync();
            connectionString = _postgres.GetConnectionString();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Shop"] = connectionString });
        builder.Services.AddProductsModule();
        builder.Services.AddErrorContract();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        App = builder.Build();
        App.UseExceptionHandler();
        App.UseStatusCodePages();
        App.UseAuthentication();
        App.UseAuthorization();
        App.MapGroup("/api/v1").WithRequestValidation().MapProductsEndpoints();

        await using (var scope = App.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
            await context.Database.MigrateAsync();
            await context.Products.ExecuteDeleteAsync();
            BooksCategoryId = await context.Categories.Where(category => category.Name == "Books").Select(category => category.Id).SingleAsync();
            var homeCategoryId = await context.Categories.Where(category => category.Name == "Home").Select(category => category.Id).SingleAsync();
            var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            context.Products.AddRange(
                Product(FirstBookId, "First book", 10m, BooksCategoryId, createdAt),
                Product(SecondBookId, "Second book", 19.95m, BooksCategoryId, createdAt),
                Product(NewestBookId, "Newest book", 29.50m, BooksCategoryId, createdAt.AddMinutes(1)),
                Product(HomeProductId, "Home product", 49.99m, homeCategoryId, createdAt.AddMinutes(2)));
            await context.SaveChangesAsync();
        }

        await App.StartAsync();
        Client = App.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null)
        {
            await App.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    private static Product Product(Guid id, string name, decimal price, Guid categoryId, DateTimeOffset createdAt) => new()
    {
        Id = id,
        Name = name,
        Description = "Catalog integration test product.",
        Price = price,
        ImagePath = "products/second book.png",
        CategoryId = categoryId,
        CreatedAt = createdAt,
    };
}
