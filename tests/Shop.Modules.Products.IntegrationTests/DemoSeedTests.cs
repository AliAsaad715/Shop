using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Seeding;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

[Collection("Products database")]
public sealed partial class DemoSeedTests(DemoSeedFixture fixture) : IClassFixture<DemoSeedFixture>
{
    private static readonly Guid[] DemoIds = DemoProducts.All.Select(product => product.Id).ToArray();

    [Fact]
    public void WarehouseSeedStocksExactlyTheDemoProducts()
    {
        // Orders need each product in both databases: the app prices it, the warehouse reserves it
        var stock = WarehouseStock();

        Assert.Equal(DemoIds.Order(), stock.Keys.Order());
        Assert.Equal(1, stock[DemoProducts.ProductX]);
    }

    [Fact]
    public async Task SeederAddsTheDemoCatalogWithAnImageEach()
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        var products = await context.Products.AsNoTracking().Where(product => DemoIds.Contains(product.Id)).ToListAsync();

        Assert.Equal(DemoIds.Order(), products.Select(product => product.Id).Order());
        Assert.Equal(products.Count, products.Select(product => product.ImagePath).Distinct().Count());
        foreach (var product in products)
        {
            var image = await File.ReadAllBytesAsync(Path.Combine(fixture.ImageRoot, product.ImagePath));
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], image[..4]);
        }
    }

    [Fact]
    public async Task SeedingAgainAddsNothingAndKeepsEdits()
    {
        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
            await context.Products.Where(product => product.Id == DemoProducts.ProductX)
                .ExecuteUpdateAsync(product => product.SetProperty(p => p.Price, 64.00m));
        }

        var imagesBefore = Directory.GetFiles(fixture.ImageRoot, "*", SearchOption.AllDirectories).Length;
        var seeder = fixture.App.Services.GetServices<IHostedService>().OfType<DemoProductSeeder>().Single();
        await seeder.StartAsync(CancellationToken.None); // what a restart does

        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
            Assert.Equal(DemoIds.Length, await context.Products.CountAsync(product => DemoIds.Contains(product.Id)));
            Assert.Equal(64.00m, await context.Products.Where(product => product.Id == DemoProducts.ProductX)
                .Select(product => product.Price).SingleAsync());
        }

        Assert.Equal(imagesBefore, Directory.GetFiles(fixture.ImageRoot, "*", SearchOption.AllDirectories).Length);
    }

    // product_id → available, as warehouse/db/002_seed.sql inserts them
    private static Dictionary<Guid, int> WarehouseStock()
    {
        var sql = File.ReadAllText(RepositoryFile(Path.Combine("warehouse", "db", "002_seed.sql")));
        return StockRow().Matches(sql).ToDictionary(
            match => Guid.Parse(match.Groups["id"].Value), match => int.Parse(match.Groups["stock"].Value));
    }

    private static string RepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Shop.slnx")))
            {
                return Path.Combine(directory.FullName, relativePath);
            }
        }

        throw new InvalidOperationException($"No Shop.slnx above {AppContext.BaseDirectory}");
    }

    [GeneratedRegex(@"\('(?<id>[0-9a-f-]{36})',\s*(?<stock>\d+)\)")]
    private static partial Regex StockRow();
}

public sealed class DemoSeedFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private readonly DirectoryInfo _images = Directory.CreateTempSubdirectory("shop-demo-images-");

    public WebApplication App { get; private set; } = null!;
    public string ImageRoot => _images.FullName;

    public async Task InitializeAsync()
    {
        // Like the other fixtures, a shared database must be disposable: its demo products are replaced
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
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = connectionString,
            ["Images:RootPath"] = ImageRoot,
            ["DemoSeed:Enabled"] = "true",
        });
        builder.Services.AddProductsModule();
        App = builder.Build();

        await using (var scope = App.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
            await context.Database.MigrateAsync();
            await DeleteDemoProductsAsync(context);
        }

        // Starting the host runs the seeder, as it does in the API
        await App.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (App is not null)
        {
            if (_postgres is null)
            {
                await using var scope = App.Services.CreateAsyncScope();
                await DeleteDemoProductsAsync(scope.ServiceProvider.GetRequiredService<ProductsDbContext>());
            }

            await App.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        _images.Delete(recursive: true);
    }

    private static Task DeleteDemoProductsAsync(ProductsDbContext context)
    {
        var ids = DemoProducts.All.Select(product => product.Id).ToArray();
        return context.Products.Where(product => ids.Contains(product.Id)).ExecuteDeleteAsync();
    }
}
