using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Shop.SharedKernel.Persistence;

/// <summary>Creates an owned context for a read without changing the request's primary/write context.</summary>
public sealed class ModuleReadDbContextFactory<TContext>(IServiceProvider services, IConfiguration configuration,
    ILogger<ModuleReadDbContextFactory<TContext>> logger)
    where TContext : DbContext
{
    public const string ReadConnectionStringName = "ShopRead";

    public async Task<TResult> ReadAsync<TResult>(DatabaseReadRoute route,
        Func<TContext, CancellationToken, Task<TResult>> query, CancellationToken ct)
    {
        try
        {
            await using var context = Create(route);
            return await query(context, ct);
        }
        catch (Exception error) when (route == DatabaseReadRoute.Replica && !ct.IsCancellationRequested
            && (error is NpgsqlException { IsTransient: true } or TimeoutException
                || error is InvalidOperationException { InnerException: NpgsqlException { IsTransient: true } }))
        {
            // A standby may disappear after a pooled connection was selected. Retry this read once, as a whole.
            logger.LogWarning(error, "Replica read failed; retrying the read on the primary");
            await using var context = Create(DatabaseReadRoute.Primary);
            return await query(context, ct);
        }
    }

    private TContext Create(DatabaseReadRoute route)
    {
        var connection = configuration.GetConnectionString(route == DatabaseReadRoute.Replica
            ? ReadConnectionStringName : ModuleDbContextExtensions.ConnectionStringName)
            ?? throw new InvalidOperationException("The selected database connection is not configured.");
        // Clone the normal options, preserving the module's schema/history and all diagnostic/test interceptors.
        var options = new DbContextOptionsBuilder<TContext>(services.GetRequiredService<DbContextOptions<TContext>>())
            .UseNpgsql(connection).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
        return ActivatorUtilities.CreateInstance<TContext>(services, options);
    }
}
