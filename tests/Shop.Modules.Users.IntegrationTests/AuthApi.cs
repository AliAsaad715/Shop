using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shop.Modules.Users.Data;
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
            ["Admin:Email"] = null,
            ["Admin:Password"] = null,
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
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGroup("/api/v1").WithRequestValidation().MapUsersEndpoints();
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
