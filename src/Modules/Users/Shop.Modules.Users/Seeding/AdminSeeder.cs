using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Modules.Users.Domain;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Persistence;

namespace Shop.Modules.Users.Seeding;

// Called explicitly by the migrator after all module migrations have completed.
internal sealed class AdminSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<AdminSeedOptions> options,
    ILogger<AdminSeeder> logger) : IModuleSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

    // A weak seed password should fail initialization rather than silently leave the API without an admin
    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {action}: {string.Join("; ", result.Errors.Select(error => error.Description))}");
        }
    }
}
