using Xunit;

namespace Shop.ArchitectureTests;

/// <summary>
/// Guards the module discovery the boundary tests rely on, so a module can't be skipped silently.
/// </summary>
public sealed class ModuleDiscoveryTests
{
    [Fact]
    public void EveryModuleAssemblyFollowsTheModuleNaming()
    {
        Assert.True(
            ShopAssemblies.UnrecognisedModuleAssemblies.Count == 0,
            "These assemblies aren't named Shop.Modules.<Module> or Shop.Modules.<Module>.Contracts, so no rule checks them: "
                + string.Join(", ", ShopAssemblies.UnrecognisedModuleAssemblies));
    }

    [Fact]
    public void EveryModuleFolderIsLoaded()
    {
        var modulesFolder = Path.Combine(FindRepositoryRoot(), "src", "Modules");
        var folders = Directory.GetDirectories(modulesFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        var loaded = ShopAssemblies.Modules.Select(module => module.Name).ToList();

        var missing = folders.Except(loaded).ToList();
        Assert.True(
            missing.Count == 0,
            "These modules exist under src/Modules but their assemblies aren't loaded, so no rule checks them "
                + "(reference them from Shop.Api): " + string.Join(", ", missing));
        Assert.NotEmpty(loaded);
    }

    [Fact]
    public void ModulesWithStorageExposeTheirDbContexts()
    {
        // If discovery found no DbContext at all, the DbContext rules would pass without checking anything
        var dbContexts = ShopAssemblies.ModuleImplementations.SelectMany(PersistenceTypes.DbContexts).ToList();

        Assert.NotEmpty(dbContexts);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Shop.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No Shop.slnx above {AppContext.BaseDirectory}; run the tests from the repository.");
    }
}
