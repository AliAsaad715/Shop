using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Shop.Modules.Products.Contracts;

namespace Shop.Modules.Products.UnitTests;

/// <summary>
/// The inventory client registered by <c>AddProductsModule</c> exactly as in production, talking to a
/// <see cref="FakeWarehouse"/> hosted in memory. Only the connection under the HTTP/2 client is swapped.
/// </summary>
internal sealed class InventoryTestbed : IAsyncDisposable
{
    private readonly WebApplication? _warehouseHost;
    private readonly ServiceProvider _app;
    private readonly AsyncServiceScope _scope;

    private InventoryTestbed(WebApplication? warehouseHost, ServiceProvider app, FakeWarehouse warehouse)
    {
        _warehouseHost = warehouseHost;
        _app = app;
        _scope = app.CreateAsyncScope();
        Warehouse = warehouse;
        Inventory = _scope.ServiceProvider.GetRequiredService<IInventoryService>();
    }

    public FakeWarehouse Warehouse { get; }

    public IInventoryService Inventory { get; }

    public static async Task<InventoryTestbed> StartAsync(IDictionary<string, string?>? settings = null)
    {
        var warehouse = new FakeWarehouse();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(warehouse);

        var host = builder.Build();
        host.MapGrpcService<FakeWarehouse>();
        await host.StartAsync();

        var server = host.GetTestServer();
        return new InventoryTestbed(host, BuildApp("http://warehouse.test", settings, server.CreateHandler), warehouse);
    }

    /// <summary>The client pointed at a port nothing listens on, as when the warehouse container is stopped.</summary>
    public static InventoryTestbed WithStoppedWarehouse(IDictionary<string, string?>? settings = null) =>
        new(null, BuildApp($"http://127.0.0.1:{ClosedPort()}", settings, primaryHandler: null), new FakeWarehouse());

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _app.DisposeAsync();
        if (_warehouseHost is not null)
        {
            await _warehouseHost.DisposeAsync();
        }
    }

    private static ServiceProvider BuildApp(
        string address, IDictionary<string, string?>? settings, Func<HttpMessageHandler>? primaryHandler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Inventory:Address"] = address })
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddProductsModule();

        if (primaryHandler is not null)
        {
            // Runs after the module's own handler setup, so the test server replaces the socket connection
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = primaryHandler()));
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // A port that was free a moment ago, so connecting to it is refused
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
