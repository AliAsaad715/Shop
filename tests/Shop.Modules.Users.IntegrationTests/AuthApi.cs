using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shop.Modules.Users.Data;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Validation;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Users.IntegrationTests;

// Existing tests can use one external database; keep these HTTP tests and their temporary admins separate in time.
[CollectionDefinition("Users HTTP", DisableParallelization = true)]
public sealed class UsersHttpCollection;

public sealed class AuthApi : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private WebApplication _app = null!;
    private readonly List<Guid> _createdUsers = [];

    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _app.Services;
    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    public int LoginPermitLimit { get; init; } = 1000;
    public int LoginWindowSeconds { get; init; } = 60;

    public void TrackUser(Guid id) => _createdUsers.Add(id);

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("SHOP_USERS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _postgres = new PostgreSqlBuilder("postgres:18").Build();
            await _postgres.StartAsync();
            connectionString = _postgres.GetConnectionString();
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = connectionString,
            ["Jwt:SigningKey"] = SigningKey,
            ["Jwt:Issuer"] = "shop",
            ["Jwt:Audience"] = "shop-api",
            ["Jwt:LifetimeMinutes"] = "15",
            ["Admin:Email"] = null,
            ["Admin:Password"] = null,
            ["LoginRateLimit:PermitLimit"] = LoginPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["LoginRateLimit:WindowSeconds"] = LoginWindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.Logging.ClearProviders();
        builder.Services.AddUsersModule();
        builder.Services.AddErrorContract();
        _app = builder.Build();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UsersDbContext>().Database.MigrateAsync();
        }

        _app.UseExceptionHandler();
        _app.UseStatusCodePages();
        // TestServer has no real client connection. This header exists only in this test host.
        _app.Use((context, next) =>
        {
            if (System.Net.IPAddress.TryParse(context.Request.Headers["X-Test-Client-IP"], out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            return next(context);
        });
        _app.UseRouting();
        _app.UseRateLimiter();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGroup("/api/v1").WithRequestValidation().MapUsersEndpoints();
        _app.MapGet("/test/default", () => "authenticated");
        _app.MapGet("/test/admin", () => "admin").RequireAuthorization(Policies.AdminOnly);
        _app.MapGet("/test/customer", () => "customer").RequireAuthorization(Policies.CustomerOnly);
        _app.MapGet("/test/current", (ICurrentUser user) => new
        {
            user.IsAuthenticated,
            user.UserId,
            user.Email,
            user.Roles,
            IsAdmin = user.IsInRole(Roles.Admin),
        });
        _app.MapGet("/test/public", (ICurrentUser user) => new { user.IsAuthenticated, user.UserId, user.Email, user.Roles })
            .AllowAnonymous();
        await _app.StartAsync();
        Client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
            await context.Users.Where(user => _createdUsers.Contains(user.Id)).ExecuteDeleteAsync();
        }

        await _app.DisposeAsync();
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }
}
