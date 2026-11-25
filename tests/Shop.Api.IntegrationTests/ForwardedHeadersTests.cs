using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Api.Security;
using Shop.Testing;
using Xunit;

namespace Shop.Api.IntegrationTests;

// Send real TestServer connection addresses through the actual Program pipeline, without public diagnostic routes.
public sealed class ForwardedHeadersTests(ForwardedHeadersFixture fixture) : IClassFixture<ForwardedHeadersFixture>
{
    [Theory]
    [InlineData("172.30.50.20", "198.51.100.10")]
    [InlineData("::ffff:172.30.50.20", "198.51.100.11")]
    [InlineData("172.30.50.21", "2001:db8::12")]
    public async Task TrustedPeerSetsTheClientAddressAndHttpsBeforeSecurityMiddleware(string peer, string client)
    {
        var context = await fixture.Factory.SendAsync(peer, client, "https");
        Assert.Equal(IPAddress.Parse(client), context.Connection.RemoteIpAddress);
        Assert.Equal("https", context.Request.Scheme);
        Assert.Equal("shop.proxy.test:8080", context.Request.Host.Value);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("max-age=", context.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("172.30.51.20")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task UntrustedSourcesIncludingDefaultLoopbacksCannotForward(string peer)
    {
        var context = await fixture.Factory.SendAsync(peer, "198.51.100.20", "https", forwardedHost: "forged.example");
        Assert.Equal(IPAddress.Parse(peer), context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
        Assert.Equal("shop.proxy.test:8080", context.Request.Host.Value);
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Theory]
    [InlineData("172.30.50.20")]
    [InlineData("127.0.0.1")]
    public async Task DisabledForwardingPreservesDirectConnections(string peer)
    {
        await using var factory = new ForwardedHeadersFactory(enabled: false);
        var context = await factory.SendAsync(peer, "198.51.100.30", "https");
        Assert.Equal(IPAddress.Parse(peer), context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task OnlyTheRightmostHopIsProcessed()
    {
        // The rightmost address is trusted too, so only the one-hop limit prevents consuming the forged left hop.
        var context = await fixture.Factory.SendAsync("172.30.50.20", "192.0.2.1, 172.30.50.40", "http, https");
        Assert.Equal(IPAddress.Parse("172.30.50.40"), context.Connection.RemoteIpAddress);
        Assert.Equal("https", context.Request.Scheme);
    }

    [Theory]
    [InlineData("not-an-address", "https")]
    [InlineData("198.51.100.50", "https://forged.example")]
    [InlineData("198.51.100.50, 198.51.100.51", "https")]
    [InlineData("198.51.100.50", "http, https")]
    [InlineData("198.51.100.50", null)]
    [InlineData(null, "https")]
    public async Task MalformedOrAsymmetricHeadersDoNotPartiallyChangeTheContext(string? client, string? scheme)
    {
        var context = await fixture.Factory.SendAsync("172.30.50.20", client, scheme);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(IPAddress.Parse("172.30.50.20"), context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task OpenApiUsesTheForwardedSchemeAndOriginalHostWithPort(string scheme)
    {
        var context = await fixture.Factory.SendAsync("172.30.50.20", "198.51.100.60", scheme,
            path: "/openapi/v1.json", forwardedHost: "forged.example");
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal($"{scheme}://shop.proxy.test:8080/", document.RootElement.GetProperty("servers")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task TrustedHttpsDoesNotLoopThroughHttpsRedirection()
    {
        await using var factory = new ForwardedHeadersFactory(httpsPort: 7443);
        var trusted = await factory.SendAsync("172.30.50.20", "198.51.100.70", "https");
        Assert.Equal(StatusCodes.Status200OK, trusted.Response.StatusCode);
        Assert.False(trusted.Response.Headers.ContainsKey("Location"));
        var untrusted = await factory.SendAsync("192.0.2.70", "198.51.100.70", "https");
        Assert.Equal(StatusCodes.Status307TemporaryRedirect, untrusted.Response.StatusCode);
        Assert.Equal("https://shop.proxy.test:7443/health", untrusted.Response.Headers.Location);
    }

    [Fact]
    public async Task ForwardedClientsHaveSeparateQuotasAndLogsUseTheNormalizedPartition()
    {
        const string client = "198.51.100.80";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var accepted = await fixture.Factory.SendAsync("172.30.50.20", client, "http", path: "/api/v1/auth/login");
            Assert.Equal(StatusCodes.Status401Unauthorized, accepted.Response.StatusCode);
            await AssertPartitionLogAsync(accepted, client);
        }
        var limited = await fixture.Factory.SendAsync("172.30.50.21", "::ffff:198.51.100.80", "http", path: "/api/v1/auth/login");
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.Response.StatusCode);
        Assert.True(limited.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal("application/problem+json", limited.Response.ContentType);
        await AssertPartitionLogAsync(limited, client);
        var otherClient = await fixture.Factory.SendAsync("172.30.50.20", "198.51.100.81", "http", path: "/api/v1/auth/login");
        Assert.Equal(StatusCodes.Status401Unauthorized, otherClient.Response.StatusCode);
        await AssertPartitionLogAsync(otherClient, "198.51.100.81");
    }

    [Fact]
    public async Task ChangingSpoofedHeadersFromAnUntrustedPeerCannotResetItsQuota()
    {
        const string peer = "192.0.2.90";
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var context = await fixture.Factory.SendAsync(peer, $"198.51.100.{100 + attempt}", "http", path: "/api/v1/auth/login");
            Assert.Equal(attempt <= 5 ? StatusCodes.Status401Unauthorized : StatusCodes.Status429TooManyRequests,
                context.Response.StatusCode);
            await AssertPartitionLogAsync(context, peer);
        }
    }

    private async Task AssertPartitionLogAsync(HttpContext context, string expectedIp)
    {
        using var problem = await JsonDocument.ParseAsync(context.Response.Body);
        var traceId = problem.RootElement.GetProperty("traceId").GetString();
        var entry = Assert.Single(fixture.Factory.Logs.Entries, entry => entry.TraceId == traceId);
        Assert.Equal(expectedIp, entry.ClientIp);
    }
}

public sealed class ReverseProxyConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("172.30.50.0/invalid")]
    [InlineData("172.30.50.0/33")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public async Task EnabledForwardingRequiresAnExplicitValidNetwork(string? network)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Enabled"] = "true", ["ReverseProxy:TrustedNetworks:0"] = network,
        });
        builder.Services.AddReverseProxyHeaders();
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("ReverseProxy:TrustedNetworks", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitPolicyOverridesFrameworkTrustAllConfiguration(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Enabled"] = enabled.ToString(),
            ["ReverseProxy:TrustedNetworks:0"] = "172.30.50.0/24",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration).AddReverseProxyHeaders();
        // Framework configuration may be registered after the application; its permissive shortcut must not win.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.All;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(enabled ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto : ForwardedHeaders.None,
            options.ForwardedHeaders);
        Assert.Empty(options.KnownProxies);
        Assert.Equal(enabled ? 1 : 0, options.KnownIPNetworks.Count);
        Assert.Equal(1, options.ForwardLimit);
        Assert.True(options.RequireHeaderSymmetry);
    }
}

public sealed class ForwardedHeadersFixture : IAsyncLifetime
{
    private readonly RedisTestServer _redis = new();
    internal ForwardedHeadersFactory Factory { get; private set; } = null!;
    internal string RedisConnection => _redis.ConnectionString;
    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        Factory = new ForwardedHeadersFactory(redisConnection: RedisConnection);
    }
    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        await _redis.DisposeAsync();
    }
}

internal sealed class ForwardedHeadersFactory(
    bool enabled = true, int? httpsPort = null, string redisConnection = "127.0.0.1:1",
    string? instanceName = null, int windowSeconds = 60) : WebApplicationFactory<global::Program>
{
    internal PartitionLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(Logs));
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Enabled"] = enabled.ToString(), ["ReverseProxy:TrustedNetworks:0"] = "172.30.50.0/24",
            ["AllowedHosts"] = "*",
            ["ConnectionStrings:Shop"] = "Host=127.0.0.1;Port=1;Database=unused_proxy_test;Username=unused;Timeout=1",
            ["ReadRouting:Enabled"] = "false", ["CatalogCache:Enabled"] = "false", ["DemoSeed:Enabled"] = "false",
            ["Admin:Email"] = "", ["Admin:Password"] = "",
            ["Jwt:SigningKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ["Jwt:Issuer"] = "shop", ["Jwt:Audience"] = "shop-api", ["Jwt:LifetimeMinutes"] = "15",
            ["Inventory:Address"] = "http://127.0.0.1:1", ["RabbitMq:Host"] = "127.0.0.1", ["RabbitMq:Port"] = "1",
            ["RabbitMq:UserName"] = "shop", ["RabbitMq:Password"] = "shop", ["RabbitMq:ConnectTimeout"] = "00:00:01",
            ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
            ["LoginRateLimit:PermitLimit"] = "5",
            ["ConnectionStrings:Redis"] = redisConnection,
            ["INSTANCE_NAME"] = instanceName,
            ["LoginRateLimit:WindowSeconds"] = windowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        if (httpsPort is { } port) builder.ConfigureServices(services => services.AddHttpsRedirection(options => options.HttpsPort = port));
    }

    internal Task<HttpContext> SendAsync(string peer, string? client, string? scheme, string path = "/health", string? forwardedHost = null) =>
        Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.TraceIdentifier = Guid.NewGuid().ToString("N");
            context.Request.Method = "GET";
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("shop.proxy.test", 8080);
            context.Request.Path = path;
            if (client is not null) context.Request.Headers["X-Forwarded-For"] = client;
            if (scheme is not null) context.Request.Headers["X-Forwarded-Proto"] = scheme;
            if (forwardedHost is not null) context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
            if (path == "/api/v1/auth/login")
            {
                context.Request.Method = "POST";
                var body = Encoding.UTF8.GetBytes("{\"email\":\"\",\"password\":\"\"}");
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = body.Length;
                context.Request.Body = new MemoryStream(body);
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyAvailable());
            }
        });

    private sealed class BodyAvailable : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}

internal sealed class PartitionLogs : ILoggerProvider
{
    internal ConcurrentQueue<(string ClientIp, string TraceId)> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Collector(categoryName, Entries);
    public void Dispose() { }

    private sealed class Collector(string category, ConcurrentQueue<(string, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (category != "Shop.Modules.Users.Auth.LoginRateLimitPolicy" || state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var fields = values.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (!fields.ContainsKey("TraceId")) return;
            entries.Enqueue((fields["ClientIp"]!.ToString()!, fields["TraceId"]!.ToString()!));
        }
    }
}
