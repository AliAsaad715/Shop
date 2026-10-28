using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Api.ConformanceTests;

[CollectionDefinition("API conformance")]
public sealed class ConformanceCollection : ICollectionFixture<ConformanceFixture>;

public sealed class ConformanceFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    internal ConformanceApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("SHOP_CONFORMANCE_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _postgres = new PostgreSqlBuilder("postgres:18").Build();
            await _postgres.StartAsync();
            connectionString = _postgres.GetConnectionString();
        }

        Factory = new ConformanceApiFactory(connectionString);
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://shop.test"),
            AllowAutoRedirect = false,
        });
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }
}

internal sealed class ConformanceApiFactory(string connectionString) : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = connectionString,
            ["Admin:Email"] = string.Empty,
            ["Admin:Password"] = string.Empty,
            ["AllowedHosts"] = "*",
        }));
        builder.ConfigureTestServices(services =>
        {
            // Supplies an authenticated identity only for Cache-Control tests, not JWT validation or role enforcement.
            services.AddAuthentication("Conformance")
                .AddScheme<AuthenticationSchemeOptions, ConformanceAuthentication>("Conformance", _ => { });
            services.AddSingleton<AuthenticationObservation>();
            services.AddSingleton<IStartupFilter, ConformanceAuthenticationStartup>();
        });
    }
}

internal sealed class AuthenticationObservation
{
    public int AuthenticatedRequests;
}

internal sealed class ConformanceAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AuthenticationObservation observation)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-Conformance-User"] != "customer")
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        Interlocked.Increment(ref observation.AuthenticatedRequests);
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "conformance-customer") }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

internal sealed class ConformanceAuthenticationStartup : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseAuthentication();
        next(app);
    };
}
