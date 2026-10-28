using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shop.Modules.Users.Data;
using Shop.Modules.Users.Domain;
using Shop.SharedKernel.Auth;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Users.IntegrationTests;

public sealed class UsersIdentityTests
{
    private const string AdminEmail = "admin@shop.test";
    private const string AdminPassword = "Admin123!";

    [Fact]
    public async Task MigrationSeedsRolesAndStartupSeedsOneAdmin()
    {
        // An explicit connection must point to a disposable database: this test creates and changes users.
        var connectionString = Environment.GetEnvironmentVariable("SHOP_USERS_TEST_CONNECTION");
        await using var postgres = string.IsNullOrWhiteSpace(connectionString)
            ? new PostgreSqlBuilder("postgres:18").Build()
            : null;
        if (postgres is not null)
        {
            await postgres.StartAsync();
            connectionString = postgres.GetConnectionString();
        }

        await using var services = BuildServices(connectionString!, AdminPassword);
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
            Assert.False(context.Database.HasPendingModelChanges());
            await context.Database.MigrateAsync();

            var roles = await context.Roles.Select(role => role.Name).OrderBy(name => name).ToListAsync();
            Assert.Equal(new[] { Roles.Admin, Roles.Customer }, roles);
        }

        await StartHostedServicesAsync(services);
        await AssertSingleAdminAsync(services, AdminPassword);

        // A restart with a different configured password neither duplicates the admin nor resets its password
        await using var restarted = BuildServices(connectionString!, "Different123!");
        await StartHostedServicesAsync(restarted);
        await AssertSingleAdminAsync(restarted, AdminPassword);

        // The unique index backs RequireUniqueEmail even when two registrations race past the check
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
            context.Users.Add(new User
            {
                UserName = "someone-else",
                NormalizedUserName = "SOMEONE-ELSE",
                Email = AdminEmail,
                NormalizedEmail = AdminEmail.ToUpperInvariant(),
                SecurityStamp = Guid.NewGuid().ToString(),
            });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
    }

    private static ServiceProvider BuildServices(string connectionString, string adminPassword)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Shop"] = connectionString,
                ["Admin:Email"] = AdminEmail,
                ["Admin:Password"] = adminPassword,
            })
            .Build();

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddUsersModule()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task StartHostedServicesAsync(IServiceProvider services)
    {
        foreach (var hostedService in services.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    private static async Task AssertSingleAdminAsync(IServiceProvider services, string password)
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var admins = await users.GetUsersInRoleAsync(Roles.Admin);
        var admin = Assert.Single(admins);
        Assert.Equal(AdminEmail, admin.Email);
        Assert.Equal(7, admin.Id.Version);
        Assert.True(await users.CheckPasswordAsync(admin, password));
    }
}
