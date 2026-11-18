using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using RabbitMQ.Client;
using Shop.Messaging;
using Shop.Modules.Notifications.Contracts.Realtime;
using Shop.SharedKernel.Auth;
using Xunit;
using Xunit.Abstractions;

namespace Shop.Api.IntegrationTests;

[Collection(R409Collection.Name)]
public sealed class R409EndToEndTests(R409ApiFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(90));
    private R409ApiFactory _api = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        try
        {
            _api = await fixture.CreateApiAsync(_timeout.Token);
            _client = _api.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = R409ApiFactory.BaseAddress, AllowAutoRedirect = false,
            });
            _client.Timeout = TimeSpan.FromSeconds(10);
            await ReadyAsync(_timeout.Token);
            await fixture.ResetQueuesAsync(_timeout.Token);
        }
        catch
        {
            await fixture.DiagnosticsAsync(output);
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_api is not null) await _api.DisposeAsync();
        _timeout.Dispose();
    }

    [Fact]
    public Task AnHttpOrderIsSavedAndPublishedByTheBackgroundRelayToRabbitMqAndTheAdmin() => RunAsync(async ct =>
    {
        var customer = Guid.NewGuid();
        await using var admin = _api.Connection("/hubs/admin", Guid.NewGuid(), Roles.Admin);
        var inbox = new Inbox(admin, RealtimeEvents.OrderPlaced);
        await _api.StartAsync(admin, ct);

        var orderId = await PlaceAsync(customer, ct);
        var saved = await _api.OutboxAsync(orderId, ShopTopology.RoutingKeys.OrderPlaced, ct);
        var body = JsonDocument.Parse(saved.Payload).RootElement;
        Assert.Equal(orderId, body.GetProperty("orderId").GetGuid());
        Assert.Equal(customer, body.GetProperty("customerId").GetGuid());
        Assert.Equal($"{customer:N}@shop.test", body.GetProperty("customerEmail").GetString());
        Assert.Equal(39.90m, body.GetProperty("total").GetDecimal());

        var published = await PublishedAsync(orderId, ct);
        AssertStoredCopy(published, await fixture.EmailAsync(ct));
        var notification = await inbox.ReadAsync(ct);
        Assert.Equal(orderId, notification.GetProperty("orderId").GetGuid());
        Assert.Equal(saved.Id, notification.GetProperty("messageId").GetGuid());
        Assert.Equal(0, notification.GetProperty("version").GetInt32());
    });

    [Fact]
    public Task AnHttpOrderReturns201WhileRabbitMqIsDownAndItsOutboxPublishesAfterRecovery() => RunAsync(async ct =>
    {
        var needsRestart = false;
        try
        {
            await fixture.BrokerApplicationAsync(start: false, ct);
            needsRestart = true;
            await R409ApiFixture.WaitAsync(_ => Task.FromResult(!_api.IsReady), "the API to observe the outage", ct);
            var customer = Guid.NewGuid();

            // RabbitMQ stays stopped until AFTER the request has completed and its durable state is checked.
            var orderId = await PlaceAsync(customer, ct);
            var pending = await _api.OutboxAsync(orderId, ShopTopology.RoutingKeys.OrderPlaced, ct);
            Assert.Null(pending.PublishedAt);

            await fixture.BrokerApplicationAsync(start: true, ct);
            needsRestart = false;
            var published = await PublishedAsync(orderId, ct);
            Assert.Equal(pending.Id, published.Id);
            AssertStoredCopy(published, await fixture.EmailAsync(ct));
            await ReadyAsync(ct);

            // The exclusive live queue may recover after the relay publishes. The durable email copy proves
            // no event loss; an HTTP snapshot supplies state missed during the live subscription's gap.
            using var request = Request(HttpMethod.Get, $"/api/v1/orders/{orderId}", customer);
            using var response = await _client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var snapshot = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(orderId, snapshot.GetProperty("id").GetGuid());
            Assert.Equal("Pending", snapshot.GetProperty("status").GetString());
        }
        finally
        {
            if (needsRestart)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await fixture.BrokerApplicationAsync(start: true, cleanup.Token);
            }
        }
    });

    [Fact]
    public Task HttpStatusChangesReachTheirOwnerWithoutLeakingToAnotherWorkingConnection() => RunAsync(async ct =>
    {
        var customerA = Guid.NewGuid();
        var customerB = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await using var a = _api.Connection("/hubs/orders", customerA);
        await using var b = _api.Connection("/hubs/orders", customerB);
        await using var admin = _api.Connection("/hubs/admin", adminId, Roles.Admin);
        var aInbox = new Inbox(a, RealtimeEvents.OrderStatusChanged);
        var bInbox = new Inbox(b, RealtimeEvents.OrderStatusChanged);
        var adminInbox = new Inbox(admin, RealtimeEvents.OrderPlaced);
        foreach (var connection in new[] { a, b, admin }) await _api.StartAsync(connection, ct);

        var orderA = await PlaceAsync(customerA, ct);
        Assert.Equal(orderA, (await adminInbox.ReadAsync(ct)).GetProperty("orderId").GetGuid());
        await ConfirmAsync(orderA, adminId, ct);
        var updateA = await aInbox.ReadAsync(ct);
        var change = await _api.OutboxAsync(orderA, ShopTopology.RoutingKeys.OrderStatusChanged, ct);
        Assert.Equal(orderA, updateA.GetProperty("orderId").GetGuid());
        Assert.Equal(change.Id, updateA.GetProperty("messageId").GetGuid());
        Assert.Equal("Pending", updateA.GetProperty("fromStatus").GetString());
        Assert.Equal("Confirmed", updateA.GetProperty("status").GetString());
        Assert.Equal(1, updateA.GetProperty("version").GetInt32());

        var orderB = await PlaceAsync(customerB, ct);
        Assert.Equal(orderB, (await adminInbox.ReadAsync(ct)).GetProperty("orderId").GetGuid());
        await ConfirmAsync(orderB, adminId, ct);
        // These events pass through the same sequential live consumer. B's own update is a barrier: an A update
        // leaked to B would be ahead of it in B's inbox, failing this assertion without a sleep or negative timeout.
        var updateB = await bInbox.ReadAsync(ct);
        Assert.Equal(orderB, updateB.GetProperty("orderId").GetGuid());
        Assert.Equal("Confirmed", updateB.GetProperty("status").GetString());
        Assert.Equal(1, updateB.GetProperty("version").GetInt32());
    });

    private async Task<Guid> PlaceAsync(Guid customer, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "/api/v1/orders", customer);
        request.Content = JsonContent.Create(new
        {
            lines = new[] { new { productId = R409ApiFactory.R409Catalog.Book.Id, quantity = 2 } },
        });
        using var response = await _client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("id").GetGuid();
    }

    private async Task ConfirmAsync(Guid orderId, Guid admin, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Patch, $"/api/v1/orders/{orderId}/status", admin, Roles.Admin);
        request.Content = JsonContent.Create(new { status = "Confirmed" });
        using var response = await _client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, Guid user, string role = Roles.Customer)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _api.Token(user, role));
        return request;
    }

    private Task ReadyAsync(CancellationToken ct) =>
        R409ApiFixture.WaitAsync(_ => Task.FromResult(_api.IsReady), "the real API's live consumer", ct);

    private async Task<R409ApiFactory.OutboxRow> PublishedAsync(Guid orderId, CancellationToken ct)
    {
        R409ApiFactory.OutboxRow? row = null;
        await R409ApiFixture.WaitAsync(async token =>
        {
            row = await _api.OutboxAsync(orderId, ShopTopology.RoutingKeys.OrderPlaced, token);
            return row.PublishedAt.HasValue;
        }, $"outbox publication for {orderId}", ct);
        return row!;
    }

    private static void AssertStoredCopy(R409ApiFactory.OutboxRow row, BasicGetResult stored)
    {
        Assert.NotNull(row.PublishedAt);
        Assert.True(row.Attempts >= 1);
        Assert.Equal(row.Id.ToString(), stored.BasicProperties.MessageId);
        Assert.Equal(ShopTopology.RoutingKeys.OrderPlaced, stored.BasicProperties.Type);
        Assert.Equal("application/json", stored.BasicProperties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, stored.BasicProperties.DeliveryMode);
        Assert.Equal(row.Payload, Encoding.UTF8.GetString(stored.Body.Span));
    }

    private async Task RunAsync(Func<CancellationToken, Task> scenario)
    {
        try
        {
            await scenario(_timeout.Token);
        }
        catch
        {
            output.WriteLine($"API live subscription ready: {_api.IsReady}");
            await fixture.DiagnosticsAsync(output);
            throw;
        }
    }

    private sealed class Inbox
    {
        private readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();
        public Inbox(HubConnection connection, string method) =>
            connection.On<JsonElement>(method, message => { _messages.Writer.TryWrite(message); });
        public ValueTask<JsonElement> ReadAsync(CancellationToken ct) => _messages.Reader.ReadAsync(ct);
    }
}
