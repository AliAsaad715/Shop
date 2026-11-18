using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Notifications.Realtime;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Api.IntegrationTests;

public sealed class SignalRSecurityTests(SignalRApi api) : IClassFixture<SignalRApi>
{
    [Theory]
    [InlineData("/hubs/orders", Roles.Customer)]
    [InlineData("/hubs/orders", Roles.Admin)]
    [InlineData("/hubs/admin", Roles.Admin)]
    public async Task ValidJwtConnectsOverWebSocketsUsingQueryOrHeader(string path, string role)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var queryToken in new[] { true, false })
        {
            await using var connection = api.Connection(path, api.Token(Guid.NewGuid(), role), queryToken);
            await api.StartAsync(connection, timeout.Token);
            Assert.Equal(HubConnectionState.Connected, connection.State);
        }
    }

    [Theory]
    [InlineData("/hubs/orders")]
    [InlineData("/hubs/admin")]
    public async Task MissingInvalidOrExpiredTokensCannotNegotiate(string path)
    {
        foreach (var invalid in new[] { "missing", "malformed", "signature", "issuer", "audience", "expired", "missing-sub", "malformed-sub", "missing-role" })
        {
            var token = invalid switch
            {
                "missing" => string.Empty,
                "malformed" => "not-a-jwt",
                _ => api.Token(Guid.NewGuid(), Roles.Admin, invalid),
            };
            using var response = await api.Client.PostAsync(
                $"{path}/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}", null);
            await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Common.Unauthorized);
        }
    }

    [Fact]
    public async Task CustomerCannotOpenTheAdminChannel()
    {
        var token = api.Token(Guid.NewGuid());
        using var response = await api.Client.PostAsync($"/hubs/admin/negotiate?access_token={token}", null);
        await AssertProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Common.Forbidden);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Bypass negotiate: assert authorization on the actual WebSocket handshake as well.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.DirectWebSocketAsync("/hubs/admin", token, timeout.Token));
        Assert.Equal("Incomplete handshake, status code: 403", error.Message);
    }

    [Theory]
    [InlineData("/api/v1/test/protected")]
    [InlineData("/hubs-extra/protected")]
    public async Task QueryTokensDoNotAuthenticateRestOrPrefixLookalikes(string path)
    {
        var token = api.Token(Guid.NewGuid());
        using var response = await api.Client.GetAsync($"{path}?access_token={token}");
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Common.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var authenticated = await api.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task UserMessagesReachAllOfAsConnectionsAndNoneOfBs()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var customerA = Guid.NewGuid();
        await using var a1 = api.Connection("/hubs/orders", api.Token(customerA));
        await using var a2 = api.Connection("/hubs/orders", api.Token(customerA));
        await using var b = api.Connection("/hubs/orders", api.Token(Guid.NewGuid()));
        var a1Messages = Listen(a1);
        var a2Messages = Listen(a2);
        var bMessages = Listen(b);
        await api.StartAsync(a1, timeout.Token);
        await api.StartAsync(a2, timeout.Token);
        await api.StartAsync(b, timeout.Token);

        var hub = api.Services.GetRequiredService<IHubContext<OrdersHub>>();
        await hub.Clients.User(customerA.ToString()).SendAsync("SecurityProbe", "private-a", timeout.Token);
        // Each WebSocket preserves ordering: B must see the barrier first, without a leaked private message.
        await hub.Clients.All.SendAsync("SecurityProbe", "barrier", timeout.Token);
        foreach (var messages in new[] { a1Messages, a2Messages })
        {
            Assert.Equal("private-a", await messages.ReadAsync(timeout.Token));
            Assert.Equal("barrier", await messages.ReadAsync(timeout.Token));
        }
        Assert.Equal("barrier", await bMessages.ReadAsync(timeout.Token));
        Assert.False(bMessages.TryRead(out _));
    }

    [Fact]
    public async Task EveryAdminConnectionJoinsTheGroupAndRejoinsOnANewConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var first = api.Connection("/hubs/admin", api.Token(Guid.NewGuid(), Roles.Admin));
        await using var second = api.Connection("/hubs/admin", api.Token(Guid.NewGuid(), Roles.Admin));
        var firstMessages = Listen(first);
        var secondMessages = Listen(second);
        await api.StartAsync(first, timeout.Token);
        await api.StartAsync(second, timeout.Token);
        var hub = api.Services.GetRequiredService<IHubContext<AdminHub>>();

        await hub.Clients.Group(AdminHub.GroupName).SendAsync("SecurityProbe", "first", timeout.Token);
        Assert.Equal("first", await firstMessages.ReadAsync(timeout.Token));
        Assert.Equal("first", await secondMessages.ReadAsync(timeout.Token));

        var oldId = first.ConnectionId;
        await first.StopAsync(timeout.Token);
        await api.StartAsync(first, timeout.Token);
        Assert.NotEqual(oldId, first.ConnectionId);
        await hub.Clients.Group(AdminHub.GroupName).SendAsync("SecurityProbe", "reconnected", timeout.Token);
        Assert.Equal("reconnected", await firstMessages.ReadAsync(timeout.Token));
        Assert.Equal("reconnected", await secondMessages.ReadAsync(timeout.Token));
    }

    [Theory]
    [InlineData("/hubs/orders", Roles.Customer)]
    [InlineData("/hubs/admin", Roles.Admin)]
    public async Task ExpirationClosesAnOpenWebSocketAndAFreshTokenRestoresDelivery(string path, string role)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var user = Guid.NewGuid();
        await using var connection = api.Connection(path, api.Token(user, role, lifetime: TimeSpan.FromSeconds(3)));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await api.StartAsync(connection, timeout.Token);
        await closed.Task.WaitAsync(timeout.Token);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);

        await using var fresh = api.Connection(path, api.Token(user, role));
        var messages = Listen(fresh);
        await api.StartAsync(fresh, timeout.Token);
        var recipient = path == "/hubs/admin"
            ? api.Services.GetRequiredService<IHubContext<AdminHub>>().Clients.Group(AdminHub.GroupName)
            : api.Services.GetRequiredService<IHubContext<OrdersHub>>().Clients.User(user.ToString());
        await recipient.SendAsync("SecurityProbe", "fresh", timeout.Token);
        Assert.Equal("fresh", await messages.ReadAsync(timeout.Token));
    }

    [Theory]
    [InlineData("/hubs/orders", Roles.Customer)]
    [InlineData("/hubs/admin", Roles.Admin)]
    public async Task ClientsCannotChooseAnOrderOrGroupToSubscribeTo(string path, string role)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = api.Connection(path, api.Token(Guid.NewGuid(), role));
        await api.StartAsync(connection, timeout.Token);
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", Guid.NewGuid(), timeout.Token));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinGroup", "admins", timeout.Token));
    }

    private static ChannelReader<string> Listen(HubConnection connection)
    {
        var messages = Channel.CreateUnbounded<string>();
        connection.On<string>("SecurityProbe", message => { messages.Writer.TryWrite(message); });
        return messages.Reader;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }
}
