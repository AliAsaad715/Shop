using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Modules.Users.Domain;
using Shop.SharedKernel.Auth;

namespace Shop.Modules.Users.Seeding;

// Runs when the host starts, after Program.cs has migrated the database
internal sealed class AdminSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<AdminSeedOptions> options,
    ILogger<AdminSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var admin = options.Value;
        if (string.IsNullOrWhiteSpace(admin.Email) || string.IsNullOrWhiteSpace(admin.Password))
        {
            logger.LogWarning("No admin account seeded; set Admin__Email and Admin__Password");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        // An existing account keeps its password, so restarting never undoes a password change
        var user = await users.FindByEmailAsync(admin.Email);
        if (user is null)
        {
            user = new User { UserName = admin.Email, Email = admin.Email, EmailConfirmed = true };
            EnsureSucceeded(await users.CreateAsync(user, admin.Password), "create the admin account");
            logger.LogInformation("Seeded admin account {Email}", admin.Email);
        }

        if (!await users.IsInRoleAsync(user, Roles.Admin))
        {
            EnsureSucceeded(await users.AddToRoleAsync(user, Roles.Admin), "add the admin account to the Admin role");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // A weak seed password should stop startup, not leave the API without an admin
    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {action}: {string.Join("; ", result.Errors.Select(error => error.Description))}");
        }
    }
}
