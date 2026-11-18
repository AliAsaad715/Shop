using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shop.Api.Routing;
using Shop.Modules.Notifications;
using Shop.Modules.Notifications.Realtime;
using Shop.Messaging;
using Shop.Modules.Users;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Api.IntegrationTests;

// Real JWT validation and WebSockets without database migrations or fake auth. Security tests omit the broker;
// live-delivery tests opt into messaging against their own container.
public sealed class SignalRApi : IAsyncLifetime
{
    private readonly Dictionary<string, string?>? _messagingSettings;
    private readonly string _clientName;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly ConnectedConnections _connections = new();
    private WebApplication _app = null!;

    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _app.Services;

    public SignalRApi() : this(null) { }

    internal SignalRApi(Dictionary<string, string?>? messagingSettings, string? clientName = null,
        Action<IServiceCollection>? configureServices = null)
    {
        _messagingSettings = messagingSettings;
        _clientName = clientName ?? $"signalr-tests-{Guid.NewGuid():N}";
        _configureServices = configureServices;
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = Convert.ToBase64String(_key),
            ["Jwt:Issuer"] = "shop",
            ["Jwt:Audience"] = "shop-api",
            ["Jwt:LifetimeMinutes"] = "15",
            ["Admin:Email"] = string.Empty,
            ["Admin:Password"] = string.Empty,
            ["ConnectionStrings:Shop"] = "Host=127.0.0.1;Database=unused_signalr_test;Username=postgres",
            ["ReadRouting:Enabled"] = "false",
            ["LoginRateLimit:PermitLimit"] = "5",
            ["LoginRateLimit:WindowSeconds"] = "60",
        });
        builder.Services.AddUsersModule().AddNotificationsModule();
        if (_messagingSettings is not null)
        {
            builder.Configuration.AddInMemoryCollection(_messagingSettings);
            builder.Services.AddShopMessaging(_clientName);
        }
        else
        {
            // R4-06's security tests stay independent of RabbitMQ.
            builder.Services.Remove(builder.Services.Single(service =>
                service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(LiveUpdateConsumer)));
        }
        builder.Services.AddErrorContract();
        builder.Services.AddSingleton(_connections);
        builder.Services.AddSignalR(options => options.AddFilter<ConnectedConnections>());
        _configureServices?.Invoke(builder.Services);

        _app = builder.Build();
        _app.UseExceptionHandler();
        _app.UseStatusCodePages();
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseRoutingErrors();
        _app.UseAuthorization();
        _app.MapNotificationsEndpoints();
        // Both exercise query-token rejection without touching storage. The second catches prefix lookalikes.
        _app.MapGet("/api/v1/test/protected", () => "authenticated").RequireAuthorization();
        _app.MapGet("/hubs-extra/protected", () => "authenticated").RequireAuthorization();
        await _app.StartAsync();
        Client = _app.GetTestClient();
    }

    public string Token(Guid userId, string role = Roles.Customer, string? invalid = null, TimeSpan? lifetime = null)
        => CreateToken(_key, userId, role, invalid, lifetime);

    internal static string CreateToken(byte[] key, Guid userId, string role = Roles.Customer,
        string? invalid = null, TimeSpan? lifetime = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["email"] = $"{userId:N}@shop.test",
            ["role"] = role,
            ["jti"] = Guid.NewGuid().ToString(),
        };
        if (invalid == "missing-sub") claims.Remove("sub");
        if (invalid == "malformed-sub") claims["sub"] = "not-a-user-id";
        if (invalid == "missing-role") claims.Remove("role");

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = invalid == "issuer" ? "untrusted" : "shop",
            Audience = invalid == "audience" ? "another-api" : "shop-api",
            IssuedAt = now.AddMinutes(-5),
            NotBefore = now.AddMinutes(-5),
            Expires = invalid == "expired" ? now.AddMinutes(-2) : now.Add(lifetime ?? TimeSpan.FromMinutes(15)),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(invalid == "signature" ? RandomNumberGenerator.GetBytes(32) : key),
                SecurityAlgorithms.HmacSha256),
            Claims = claims,
        });
    }

    public HubConnection Connection(string path, string token, bool queryToken = true)
        => Connection(_app.GetTestServer(), Client.BaseAddress!, path, token, queryToken);

    internal static HubConnection Connection(TestServer server, Uri baseAddress, string path, string token, bool queryToken = true)
    {
        var url = path + (queryToken ? $"?access_token={Uri.EscapeDataString(token)}" : string.Empty);
        return new HubConnectionBuilder().WithUrl(new Uri(baseAddress, url), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            if (!queryToken) options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            options.WebSocketFactory = async (context, ct) =>
            {
                var socket = server.CreateWebSocketClient();
                if (!queryToken)
                {
                    socket.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
                }
                return await socket.ConnectAsync(context.Uri, ct);
            };
        }).Build();
    }

    internal Task<WebSocket> DirectWebSocketAsync(string path, string token, CancellationToken ct) =>
        _app.GetTestServer().CreateWebSocketClient().ConnectAsync(
            new Uri(Client.BaseAddress!, $"{path}?access_token={Uri.EscapeDataString(token)}"), ct);

    public async Task StartAsync(HubConnection connection, CancellationToken ct)
    {
        await connection.StartAsync(ct);
        // The handshake may finish before OnConnectedAsync joins the admin group. Wait for the real lifecycle.
        await _connections.WaitAsync(connection.ConnectionId!, ct);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    internal sealed class ConnectedConnections : IHubFilter
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _connected = new();

        public Task WaitAsync(string connectionId, CancellationToken ct) =>
            _connected.GetOrAdd(connectionId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(ct);

        public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            await next(context);
            _connected.GetOrAdd(context.Context.ConnectionId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
        }
    }
}
