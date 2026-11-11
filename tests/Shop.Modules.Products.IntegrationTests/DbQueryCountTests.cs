using System.Globalization;
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
using Shop.Modules.Products.Contracts;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Domain;
using Shop.SharedKernel.Diagnostics;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Validation;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

// The development-only X-Db-Queries header (task R3-04): how many SQL commands a request sent
[Collection("Products database")]
public sealed class DbQueryCountTests(DbQueryCountFixture fixture) : IClassFixture<DbQueryCountFixture>
{
    [Theory]
    [InlineData("/api/v1/categories", 1)]
    [InlineData("/api/v1/products", 2)] // the page, and its total for the page count
    [InlineData("/api/v1/products/019a2180-2000-7000-8000-000000000001", 1)] // the stock comes from the warehouse
    [InlineData("/api/v1/products/019a2180-2000-7000-8000-0000000000ff", 1)] // a 404 still made its lookup
    public async Task DevelopmentCountsEachRequestsCommands(string path, int expected)
    {
        using var response = await fixture.Development.GetAsync(path);

        Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), Header(response));
    }

    [Fact]
    public async Task EachRequestCountsOnlyItsOwnCommands()
    {
        for (var request = 0; request < 3; request++)
        {
            using var response = await fixture.Development.GetAsync("/api/v1/products");

            Assert.Equal("2", Header(response));
        }
    }

    // An ETag saves the transfer, not the database work: the product is read either way (task R3-09)
    [Fact]
    public async Task A304StillReadsTheProductOnce()
    {
        const string path = "/api/v1/products/019a2180-2000-7000-8000-000000000001";
        using var first = await fixture.Development.GetAsync(path);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        using var revalidated = await fixture.Development.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        Assert.Equal("1", Header(revalidated));
    }

    [Fact]
    public async Task ProductionSendsNoHeader()
    {
        using var response = await fixture.Production.GetAsync("/api/v1/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(DbQueryCountHeaderExtensions.HeaderName));
    }

    private static string Header(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues(DbQueryCountHeaderExtensions.HeaderName));
}

// Two hosts on one database, alike except for the environment, set up as Program.cs sets up the app
public sealed class DbQueryCountFixture : IAsyncLifetime
{
    private static readonly Guid ProductId = Guid.Parse("019a2180-2000-7000-8000-000000000001");

    private PostgreSqlContainer? _postgres;
    private WebApplication? _development;
    private WebApplication? _production;

    public HttpClient Development { get; private set; } = null!;
    public HttpClient Production { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("SHOP_PRODUCTS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _postgres = new PostgreSqlBuilder("postgres:18").Build();
            await _postgres.StartAsync();
            connectionString = _postgres.GetConnectionString();
        }

        _development = Build(Environments.Development, connectionString);
        _production = Build(Environments.Production, connectionString);

        await using (var scope = _production.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
            await context.Database.MigrateAsync();
            await context.Products.Where(product => product.Id == ProductId).ExecuteDeleteAsync();
            context.Products.Add(new Product
            {
                Id = ProductId,
                Name = "Counted product",
                Description = "Query count integration test product.",
                Price = 5m,
                ImagePath = "products/counted.png",
                CategoryId = await context.Categories.Select(category => category.Id).FirstAsync(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await _development.StartAsync();
        await _production.StartAsync();
        Development = _development.GetTestClient();
        Production = _production.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        Development?.Dispose();
        Production?.Dispose();
        foreach (var app in new[] { _development, _production })
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    private static WebApplication Build(string environment, string connectionString)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = connectionString,
            ["ReadRouting:Enabled"] = "false",
            ["CatalogCache:Enabled"] = "false",
            // Required at startup; the warehouse itself is StockInventory below
            ["Inventory:Address"] = "http://localhost:50051",
        });
        builder.Services.AddProductsModule();
        builder.Services.AddSingleton<IInventoryService>(new StockInventory());
        builder.Services.AddErrorContract();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        var app = builder.Build();
        if (app.Environment.IsDevelopment())
        {
            app.UseDbQueryCountHeader();
        }

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGroup("/api/v1").WithRequestValidation().MapProductsEndpoints();
        return app;
    }
}
