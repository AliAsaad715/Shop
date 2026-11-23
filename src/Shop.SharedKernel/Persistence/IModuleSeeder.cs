namespace Shop.SharedKernel.Persistence;

/// <summary>A module's explicit data initialization, run after all module migrations have completed.</summary>
public interface IModuleSeeder
{
    Task SeedAsync(CancellationToken cancellationToken);
}
