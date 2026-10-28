using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Shop.SharedKernel.Persistence;

public static class ModuleMigrationExtensions
{
    private const int MaxAttempts = 10;
    private const int MaxDelaySeconds = 10;

    /// <summary>
    /// Applies every module's pending migrations, retrying while the database can't be reached,
    /// such as while its container is still starting.
    /// </summary>
    public static async Task MigrateModuleDatabasesAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ModuleMigrationExtensions));
        var contextTypes = host.Services.GetServices<ModuleDbContextRegistration>()
            .Select(registration => registration.ContextType)
            .ToArray();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = host.Services.CreateAsyncScope();
                foreach (var contextType in contextTypes)
                {
                    var context = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
                    await context.Database.MigrateAsync(cancellationToken);
                }

                return;
            }
            catch (Exception exception) when (attempt < MaxAttempts && IsTransient(exception))
            {
                var delaySeconds = Math.Min(1 << (attempt - 1), MaxDelaySeconds);
                logger.LogWarning(
                    "Database not reachable ({Reason}); attempt {Attempt} of {MaxAttempts}, retrying in {DelaySeconds} s",
                    exception.GetBaseException().Message, attempt, MaxAttempts, delaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }
    }

    // A database that is down or still starting is worth waiting for; a failing migration is not
    private static bool IsTransient(Exception exception) =>
        exception is NpgsqlException { IsTransient: true }
        || (exception.InnerException is { } inner && IsTransient(inner));
}
