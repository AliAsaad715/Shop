using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shop.Modules.Orders.Data;
using Shop.Modules.Products.Contracts;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Validation;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Orders.IntegrationTests;

[CollectionDefinition("Orders database")]
public sealed class OrdersDatabaseCollection : ICollectionFixture<OrdersApiFixture>;

public sealed class OrdersApiFixture : IAsyncLifetime
{
    public static readonly Guid CustomerA = Guid.Parse("019a2180-3000-7000-8000-00000000000a");
    public static readonly Guid CustomerB = Guid.Parse("019a2180-3000-7000-8000-00000000000b");
    public static readonly Guid Admin = Guid.Parse("019a2180-3000-7000-8000-0000000000ad");

    private PostgreSqlContainer? _postgres;

    public WebApplication App { get; private set; } = null!;
    public RecordingInventory Inventory { get; } = new();
    public TestLogSink Logs { get; } = new();

    public async Task InitializeAsync()
    {
        // This fixture deletes every order, so an external database must be a disposable one
        var connectionString = Environment.GetEnvironmentVariable("SHOP_ORDERS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _postgres = new PostgreSqlBuilder("postgres:18").Build();
            await _postgres.StartAsync();
            connectionString = _postgres.GetConnectionString();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(Logs);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Shop"] = connectionString });
        builder.Services.AddOrdersModule();
        builder.Services.AddErrorContract();
        builder.Services.AddSingleton<IProductCatalog, FakeProductCatalog>();
        builder.Services.AddSingleton<IInventoryService>(Inventory);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, TestCurrentUser>();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy(Policies.AdminOnly, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Admin));
            options.AddPolicy(Policies.CustomerOnly, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Customer));
        });

        App = builder.Build();
        App.UseExceptionHandler();
        App.UseStatusCodePages();
        App.UseAuthentication();
        App.UseAuthorization();
        App.MapGroup("/api/v1").WithRequestValidation().MapOrdersEndpoints();

        await using (var scope = App.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            await context.Database.MigrateAsync();
            await context.Orders.ExecuteDeleteAsync();
        }

        await App.StartAsync();
    }

    public HttpClient ClientFor(Guid? userId, string? role)
    {
        var client = App.GetTestClient();
        if (userId is { } id)
        {
            client.DefaultRequestHeaders.Add("X-Test-User", id.ToString());
        }

        if (role is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Role", role);
        }

        return client;
    }

    public HttpClient Anonymous() => ClientFor(null, null);

    public HttpClient Customer(Guid customerId) => ClientFor(customerId, Roles.Customer);

    public HttpClient AdminClient() => ClientFor(Admin, Roles.Admin);

    public async Task<Guid> PlaceOrderAsync(Guid customerId, int bookQuantity = 1)
    {
        using var client = Customer(customerId);
        using var response = await client.PostAsJsonAsync("/api/v1/orders",
            new { lines = new[] { new { productId = FakeProductCatalog.Book.Id, quantity = bookQuantity } } });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    public async Task DisposeAsync()
    {
        if (App is not null)
        {
            await App.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }
}

// Captures log entries so the security-log requirement can be asserted
public sealed class TestLogSink : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

    public IReadOnlyCollection<(string Category, LogLevel Level, string Message)> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new SinkLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class SinkLogger(string category, System.Collections.Concurrent.ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
