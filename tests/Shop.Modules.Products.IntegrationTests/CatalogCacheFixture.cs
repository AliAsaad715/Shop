using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Modules.Products.Catalog;
using Shop.Modules.Products.Contracts;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Domain;
using Shop.Modules.Products.Seeding;
using Shop.SharedKernel.Diagnostics;
using Shop.SharedKernel.Caching;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Validation;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

public sealed class CatalogCacheFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    private readonly IContainer _redis = new ContainerBuilder("redis:8.10.3-alpine")
        .WithPortBinding(6379, true)
        .WithCommand("redis-server", "--save", "", "--appendonly", "no", "--maxmemory", "128mb", "--maxmemory-policy", "allkeys-lru")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private readonly DirectoryInfo _images = Directory.CreateTempSubdirectory("shop-cache-images-");
    private readonly List<WebApplication> _apps = [];
    private ConnectionMultiplexer _connection = null!;

    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public HttpClient Second { get; private set; } = null!;
    public QueryProbe Queries { get; } = new();
    public QueryProbe SecondQueries { get; } = new();
    public StockInventory Inventory { get; } = new();
    public SaveFailureInterceptor SaveFailure { get; } = new();
    public Guid Category { get; private set; }
    public Guid OtherCategory { get; private set; }
    public static Guid ProductId(int n) => Guid.Parse($"019a2180-3000-7000-8000-{n:D12}");
    public IDatabase Redis => _connection.GetDatabase();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        _connection = await ConnectionMultiplexer.ConnectAsync($"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)},allowAdmin=true");
        var bootstrap = Build(new QueryProbe(), null);
        await using var scope = bootstrap.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
        await db.Database.MigrateAsync();
        Category = await db.Categories.Where(c => c.Name == "Books").Select(c => c.Id).SingleAsync();
        OtherCategory = await db.Categories.Where(c => c.Name == "Home").Select(c => c.Id).SingleAsync();
    }

    public async Task ResetAsync(Dictionary<string, string?>? settings = null)
    {
        Client?.Dispose();
        Second?.Dispose();
        foreach (var app in _apps) await app.DisposeAsync();
        _apps.Clear();
        await Redis.ExecuteAsync("FLUSHDB");
        Inventory.Failure = null;
        Inventory.Stock.Clear();
        Inventory.Lookups.Clear();
        SaveFailure.FailNext = false;
        App = Build(Queries, settings);
        var second = Build(SecondQueries, settings);
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
            await db.Products.ExecuteDeleteAsync();
            await db.Categories.Where(c => c.Id == Category).ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, "Books"));
            db.Products.AddRange(Enumerable.Range(1, 12).Select(n => new Product
            {
                Id = ProductId(n), Name = $"Product {n}", Description = "Cached catalog test product.",
                Price = n, ImagePath = "products/test.png", CategoryId = n <= 8 ? Category : OtherCategory,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, n, 0, TimeSpan.Zero),
            }));
            await db.SaveChangesAsync();
        }
        await App.StartAsync();
        await second.StartAsync();
        Client = App.GetTestClient();
        Second = second.GetTestClient();
        Queries.Reset();
        SecondQueries.Reset();
    }

    private WebApplication Build(QueryProbe probe, Dictionary<string, string?>? overrides)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = _postgres.GetConnectionString(),
            ["ConnectionStrings:Redis"] = $"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)}",
            ["CatalogCache:Enabled"] = "true",
            ["CatalogCache:ProductTtl"] = "00:10:00", ["CatalogCache:CategoriesTtl"] = "01:00:00",
            ["CatalogCache:ListTtl"] = "00:01:00", ["CatalogCache:LocalTtl"] = "00:00:05",
            ["CatalogCache:MaxCachedPage"] = "3", ["DemoSeed:Enabled"] = "false",
            ["Images:RootPath"] = _images.FullName, ["Inventory:Address"] = "http://localhost:50051",
        });
        if (overrides is not null) builder.Configuration.AddInMemoryCollection(overrides);
        builder.Services.AddProductsModule();
        builder.Services.AddSingleton<IInventoryService>(Inventory);
        builder.Services.AddDbContext<ProductsDbContext>(o => o.AddInterceptors(probe, SaveFailure));
        builder.Services.AddErrorContract();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, UploadTestAuthentication>("Test", _ => { });
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy("AdminOnly", p => p.RequireAuthenticatedUser().RequireRole("Admin"));
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });
        var app = builder.Build();
        app.UseDbQueryCountHeader();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseProductUploads();
        app.MapGroup("/api/v1").WithRequestValidation().MapProductsEndpoints();
        _apps.Add(app);
        return app;
    }

    internal async Task UpdateAsync(Func<ProductsDbContext, Task> update)
    {
        await using var scope = App.Services.CreateAsyncScope();
        await update(scope.ServiceProvider.GetRequiredService<ProductsDbContext>());
    }

    public async Task InvalidateProductAsync(Guid id, Guid? before, Guid? after)
    {
        await using var scope = App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CatalogCacheInvalidator>().ProductChangedAsync(id, before, after);
    }

    public async Task WaitForRedisAsync(string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await Redis.KeyExistsAsync(CacheRegistration.RedisPrefix + key)) await Task.Delay(20, timeout.Token);
    }

    internal async Task SeedLegacyProductAsync(Guid id)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var found = await CatalogQueries.GetAsync(id, scope.ServiceProvider.GetRequiredService<ProductsDbContext>(), CancellationToken.None);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = $"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)}",
        }).Build();
        // Write a real pre-ETag HybridCache payload into the old deployment's namespace.
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging().AddShopCaching();
        services.PostConfigure<RedisCacheOptions>(options => options.InstanceName = "shop:r3:v1:");
        await using var legacy = services.BuildServiceProvider();
        await legacy.GetRequiredService<HybridCache>().SetAsync(CatalogCacheKeys.Product(id),
            found!.Product with { Name = "Legacy payload", Price = 777m },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(1), LocalCacheExpiration = TimeSpan.FromSeconds(1) });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await Redis.KeyExistsAsync("shop:r3:v1:" + CatalogCacheKeys.Product(id))) await Task.Delay(20, timeout.Token);
    }

    public Task SeedDemoAsync()
    {
        var seeder = new DemoProductSeeder(App.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DemoSeedOptions { Enabled = true }), App.Services.GetRequiredService<ILogger<DemoProductSeeder>>());
        return seeder.StartAsync(CancellationToken.None);
    }

    public async Task<HttpResponseMessage> CreateAsync()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Created with cache"), "name");
        form.Add(new StringContent("A newly created product."), "description");
        form.Add(new StringContent("19.95"), "price");
        form.Add(new StringContent(Category.ToString()), "categoryId");
        form.Add(new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGPQSFkFAAHuATczvmbSAAAAAElFTkSuQmCC")), "image", "test.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products") { Content = form };
        request.Headers.Add("X-Test-Role", "Admin");
        return await Client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose(); Second?.Dispose();
        foreach (var app in _apps) await app.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        await _redis.DisposeAsync();
        await _postgres.DisposeAsync();
        if (_images.FullName.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && _images.Name.StartsWith("shop-cache-images-", StringComparison.Ordinal)) _images.Delete(true);
    }
}

public sealed class QueryProbe : DbCommandInterceptor
{
    private int _count;
    private QueryGate? _gate;
    public int Count => Volatile.Read(ref _count);
    public bool FailNext { get; set; }
    public void Reset() { Interlocked.Exchange(ref _count, 0); _gate = null; FailNext = false; }
    public QueryGate PauseNext() => _gate = new();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        if (FailNext) { FailNext = false; throw new InvalidOperationException("Simulated catalog database failure."); }
        var gate = Interlocked.Exchange(ref _gate, null);
        if (gate is not null)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken);
        }
        return result;
    }
}

public sealed class QueryGate
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
