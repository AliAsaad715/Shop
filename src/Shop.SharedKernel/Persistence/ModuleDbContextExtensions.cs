using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shop.SharedKernel.Persistence;

public static class ModuleDbContextExtensions
{
    public const string ConnectionStringName = "Shop";

    /// <summary>
    /// Registers a module's DbContext on the shared database, with the module's
    /// migrations history table inside the module's own schema, so each module
    /// migrates on its own.
    /// </summary>
    /// <remarks>
    /// Add a migration for one module from the repository root:
    /// <c>dotnet ef migrations add &lt;Name&gt; --context &lt;Module&gt;DbContext --project src/Modules/&lt;Module&gt;/Shop.Modules.&lt;Module&gt; --startup-project src/Shop.Api --output-dir Data/Migrations</c>
    /// </remarks>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : DbContext
    {
        services.AddSingleton(new ModuleDbContextRegistration(typeof(TContext)));

        return services.AddDbContext<TContext>((serviceProvider, options) =>
        {
            // Read when the context is first created, so tests can override the setting
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema));
        });
    }
}
