using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Notifications.Contracts.Realtime;
using Shop.Modules.Notifications.Realtime;
using Shop.SharedKernel.Auth;
using Xunit;

namespace Shop.Api.IntegrationTests;

/// <summary>
/// The contract between the hubs and the live pages (task R4-08): the pages listen for the method names R4-07 sends
/// (<see cref="RealtimeEvents"/>), and each notification arrives as camelCase JSON with the fields the pages read.
/// </summary>
public sealed class LiveEventContractTests(SignalRApi api) : IClassFixture<SignalRApi>
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThePagesListenForTheMethodNamesTheHubsSend()
    {
        var script = File.ReadAllText(RepositoryFile("src/Shop.Api/wwwroot/app/js/session.js"));

        Assert.Contains($"export const OrderPlaced = \"{RealtimeEvents.OrderPlaced}\";", script);
        Assert.Contains($"export const OrderStatusChanged = \"{RealtimeEvents.OrderStatusChanged}\";", script);
        Assert.Contains($"export const ResyncRequired = \"{RealtimeEvents.ResyncRequired}\";", script);
    }

    [Fact]
    public async Task ANewOrderReachesAdminsWithTheFieldsTheDashboardReads()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var admin = api.Connection("/hubs/admin", api.Token(Guid.NewGuid(), Roles.Admin));
        var received = Receive(admin, RealtimeEvents.OrderPlaced);
        await api.StartAsync(admin, timeout.Token);
        var order = new OrderPlacedNotification(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, Guid.CreateVersion7(), "Pending", 59.97m, 3, PlacedAt);

        await api.Services.GetRequiredService<IHubContext<AdminHub>>().Clients.Group(AdminHub.GroupName)
            .SendAsync(RealtimeEvents.OrderPlaced, order, timeout.Token);

        // What admin.js reads from it
        var json = await received.WaitAsync(timeout.Token);
        Assert.Equal(order.OrderId, json.GetProperty("orderId").GetGuid());
        Assert.Equal(order.CustomerId, json.GetProperty("customerId").GetGuid());
        Assert.Equal("Pending", json.GetProperty("status").GetString());
        Assert.Equal(59.97m, json.GetProperty("total").GetDecimal());
        Assert.Equal(3, json.GetProperty("itemCount").GetInt32());
        Assert.Equal(PlacedAt, json.GetProperty("placedAt").GetDateTimeOffset());
        Assert.Equal(0, json.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task AStatusChangeReachesItsCustomerWithTheFieldsTheirPageReads()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var customer = Guid.NewGuid();
        await using var page = api.Connection("/hubs/orders", api.Token(customer));
        var received = Receive(page, RealtimeEvents.OrderStatusChanged);
        await api.StartAsync(page, timeout.Token);
        var change = new OrderStatusChangedNotification(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "Pending", "Confirmed", PlacedAt.AddMinutes(5));

        await api.Services.GetRequiredService<IHubContext<OrdersHub>>().Clients.User(customer.ToString())
            .SendAsync(RealtimeEvents.OrderStatusChanged, change, timeout.Token);

        // What orders.js reads from it
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
