using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shop.Modules.Products.Catalog;
using Shop.Modules.Products.Contracts;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Domain;
using Shop.SharedKernel.Caching;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Validation;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

// Task R3-07: Redis behind HybridCache's in-memory layer, as compose runs it. Each test reads products no other test
// reads, so nothing it finds was cached before it
[Collection("Products database")]
public sealed class RedisCacheTests(RedisCacheFixture fixture) : IClassFixture<RedisCacheFixture>
{
    // A second instance finds in Redis what the first loaded, without asking the database
    [Fact]
    public async Task InstancesShareWhatEitherLoaded()
    {
        var path = $"/api/v1/products/{RedisCacheFixture.ProductIds[0]}";

        Assert.Equal("MISS", await CacheAsync(fixture.First, path));
        Assert.Equal("HIT", await CacheAsync(fixture.Second, path));
    }

    // Clearing reaches Redis, not only the clearing instance's memory: the other instance, which holds no copy of its
    // own, has to load the product and the list again
    [Fact]
    public async Task ClearingOnOneInstanceReachesTheOthersThroughRedis()
    {
        var product = $"/api/v1/products/{RedisCacheFixture.ProductIds[1]}";
        var list = $"/api/v1/products?category={fixture.CategoryId}&pageSize=3";
        await CacheAsync(fixture.First, product);
        await CacheAsync(fixture.First, list);

        await fixture.First.Services.GetRequiredService<CatalogCache>()
            .ProductChangedAsync(RedisCacheFixture.ProductIds[1], fixture.CategoryId);

        Assert.Equal(("MISS", "MISS"), (await CacheAsync(fixture.Second, product), await CacheAsync(fixture.Second, list)));
    }

    // Redis is only a cache: while it's unreachable, every read goes to the database, without waiting on Redis
    [Fact]
    public async Task TheCatalogStillAnswersWhenRedisIsDown()
    {
        var path = $"/api/v1/products/{RedisCacheFixture.ProductIds[2]}";
        var clock = Stopwatch.StartNew();

        var first = await CacheAsync(fixture.WithoutRedis, path);
        var second = await CacheAsync(fixture.WithoutRedis, path);

        Assert.Equal("MISS", first);
        Assert.NotNull(second);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Two reads took {clock.Elapsed} without Redis");
    }

    private static async Task<string> CacheAsync(WebApplication app, string path)
    {
        using var client = app.GetTestClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(response.Headers.GetValues(CacheReads.HeaderName));
    }
}

// Two instances of the Products module sharing one database and one Redis, and a third whose Redis isn't there
public sealed class RedisCacheFixture : IAsyncLifetime
{
    public static readonly Guid[] ProductIds =
    [
        Guid.Parse("019a2180-3000-7000-8000-000000000001"),
        Guid.Parse("019a2180-3000-7000-8000-000000000002"),
        Guid.Parse("019a2180-3000-7000-8000-000000000003"),
    ];

    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine").Build();
    private PostgreSqlContainer? _postgres;

    public WebApplication First { get; private set; } = null!;
    public WebApplication Second { get; private set; } = null!;
    public WebApplication WithoutRedis { get; private set; } = null!;
    public Guid CategoryId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("SHOP_PRODUCTS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _postgres = new PostgreSqlBuilder("postgres:18").Build();
            await _postgres.StartAsync();
            connectionString = _postgres.GetConnectionString();
        }

        await _redis.StartAsync();
        First = Build(connectionString, _redis.GetConnectionString());
        Second = Build(connectionString, _redis.GetConnectionString());
        // Nothing listens on port 1, so every Redis command fails
        WithoutRedis = Build(connectionString, "127.0.0.1:1");

        await using (var scope = First.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
            await context.Database.MigrateAsync();
            await context.Products.Where(product => ProductIds.Contains(product.Id)).ExecuteDeleteAsync();
            CategoryId = await context.Categories.Where(category => category.Name == "Books").Select(category => category.Id).SingleAsync();
            context.Products.AddRange(ProductIds.Select(id => new Product
            {
                Id = id,
                Name = "Cached product",
                Description = "Redis cache integration test product.",
                Price = 5m,
                ImagePath = "products/cached.png",
                CategoryId = CategoryId,
                CreatedAt = DateTimeOffset.UtcNow,
            }));
            await context.SaveChangesAsync();
        }

        foreach (var app in new[] { First, Second, WithoutRedis })
        {
            await app.StartAsync();
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var app in new[] { First, Second, WithoutRedis })
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }

        await _redis.DisposeAsync();
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    // As Program.cs sets the app up, Redis included
    private static WebApplication Build(string connectionString, string redis)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = connectionString,
            ["ConnectionStrings:Redis"] = redis,
            // Required at startup; the warehouse itself is StockInventory below
            ["Inventory:Address"] = "http://localhost:50051",
        });
        builder.Services.AddProductsModule();
        builder.Services.AddRedisCache(builder.Configuration);
        builder.Services.AddSingleton<IInventoryService>(new StockInventory());
        builder.Services.AddErrorContract();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGroup("/api/v1").WithRequestValidation().MapProductsEndpoints();
        return app;
    }
}
