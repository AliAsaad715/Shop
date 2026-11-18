using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Notifications.Realtime;
using Shop.Modules.Orders.Contracts;
using Shop.SharedKernel.Auth;
using Xunit;

namespace Shop.Api.IntegrationTests;

/// <summary>
/// The contract between the hubs and the live pages (task R4-08): the pages listen for the event names the server
/// sends, and each event arrives as camelCase JSON with the fields the pages read. R4-07 sends exactly these.
/// </summary>
public sealed class LiveEventContractTests(SignalRApi api) : IClassFixture<SignalRApi>
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThePagesListenForTheEventNamesTheHubsSend()
    {
        var script = File.ReadAllText(RepositoryFile("src/Shop.Api/wwwroot/app/js/session.js"));

        Assert.Contains($"export const OrderPlaced = \"{OrderUpdates.OrderPlaced}\";", script);
        Assert.Contains($"export const OrderStatusChanged = \"{OrderUpdates.OrderStatusChanged}\";", script);
    }

    [Fact]
    public async Task ANewOrderReachesAdminsWithTheFieldsTheDashboardReads()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var admin = api.Connection("/hubs/admin", api.Token(Guid.NewGuid(), Roles.Admin));
        var received = Receive(admin, OrderUpdates.OrderPlaced);
        await api.StartAsync(admin, timeout.Token);
        var order = new OrderPlacedEvent(Guid.CreateVersion7(), Guid.CreateVersion7(), "ada@shop.test", "Pending", 59.97m,
            [new OrderPlacedLine(Guid.CreateVersion7(), "Cotton T-Shirt", 19.99m, 3, 59.97m)], 0, PlacedAt);

        await api.Services.GetRequiredService<IHubContext<AdminHub>>().Clients.Group(AdminHub.GroupName)
            .SendAsync(OrderUpdates.OrderPlaced, order, timeout.Token);

        // What admin.js reads from it
        var json = await received.WaitAsync(timeout.Token);
        Assert.Equal(order.OrderId, json.GetProperty("orderId").GetGuid());
        Assert.Equal(order.CustomerId, json.GetProperty("customerId").GetGuid());
        Assert.Equal("Pending", json.GetProperty("status").GetString());
        Assert.Equal(59.97m, json.GetProperty("total").GetDecimal());
        Assert.Equal(3, json.GetProperty("lines")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(PlacedAt, json.GetProperty("placedAt").GetDateTimeOffset());
        Assert.Equal(0, json.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task AStatusChangeReachesItsCustomerWithTheFieldsTheirPageReads()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var customer = Guid.NewGuid();
        await using var page = api.Connection("/hubs/orders", api.Token(customer));
        var received = Receive(page, OrderUpdates.OrderStatusChanged);
        await api.StartAsync(page, timeout.Token);
        var change = new OrderStatusChangedEvent(Guid.CreateVersion7(), customer, "Pending", "Confirmed", 1, Guid.NewGuid(), PlacedAt.AddMinutes(5));

        await api.Services.GetRequiredService<IHubContext<OrdersHub>>().Clients.User(customer.ToString())
            .SendAsync(OrderUpdates.OrderStatusChanged, change, timeout.Token);

        // What orders.js and admin.js read from it
        var json = await received.WaitAsync(timeout.Token);
        Assert.Equal(change.OrderId, json.GetProperty("orderId").GetGuid());
        Assert.Equal("Pending", json.GetProperty("fromStatus").GetString());
        Assert.Equal("Confirmed", json.GetProperty("status").GetString());
        Assert.Equal(1, json.GetProperty("version").GetInt32());
    }

    private static Task<JsonElement> Receive(HubConnection connection, string method)
    {
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(method, json => received.TrySetResult(json));
        return received.Task;
    }

    // A file from the repository, found by walking up from the test's output folder to Shop.slnx
    private static string RepositoryFile(string path)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Shop.slnx")))
            {
                return Path.Combine(folder.FullName, path);
            }
        }

        throw new FileNotFoundException("Shop.slnx wasn't found above the test's output folder.", path);
    }
}
