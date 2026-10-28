using System.Reflection;
using System.Text.RegularExpressions;
using ArchUnitNET.Loader;
using Microsoft.Extensions.DependencyModel;
using Architecture = ArchUnitNET.Domain.Architecture;

namespace Shop.ArchitectureTests;

/// <summary>
/// One module as two assemblies: Shop.Modules.X (internals) and Shop.Modules.X.Contracts (what others may use).
/// </summary>
internal sealed record ShopModule(string Name, Assembly Implementation, Assembly Contracts);

/// <summary>
/// Every Shop assembly the host pulls in, read from this project's deps.json rather than listed by hand,
/// so a module added to Shop.Api is checked without touching these tests.
/// </summary>
internal static partial class ShopAssemblies
{
    // deps.json rather than the bin folder: incremental builds leave renamed or removed projects' DLLs behind
    public static IReadOnlyList<Assembly> All { get; } =
    [
        .. (DependencyContext.Default ?? throw new InvalidOperationException("No deps.json for the test assembly."))
            .RuntimeLibraries
            .Where(library => library.Type == "project" && library.Name.StartsWith("Shop.", StringComparison.Ordinal))
            .Select(library => library.Name)
            .Where(name => name != typeof(ShopAssemblies).Assembly.GetName().Name)
            .Select(Load)
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal),
    ];

    public static Assembly SharedKernel { get; } = All.Single(assembly => Name(assembly) == "Shop.SharedKernel");

    /// <summary>Shop.Modules.* assemblies whose name doesn't fit the module naming; a test keeps this empty.</summary>
    public static IReadOnlyList<string> UnrecognisedModuleAssemblies { get; } =
    [
        .. All
            .Select(Name)
            .Where(name => name.StartsWith("Shop.Modules.", StringComparison.Ordinal) && !ModuleAssemblyName().IsMatch(name)),
    ];

    public static IReadOnlyList<ShopModule> Modules { get; } =
    [
        .. All
            .Select(assembly => ModuleAssemblyName().Match(Name(assembly)))
            .Where(match => match.Success)
            .Select(match => match.Groups["module"].Value)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Select(name => new ShopModule(
                name,
                All.SingleOrDefault(assembly => Name(assembly) == $"Shop.Modules.{name}")
                    ?? throw new InvalidOperationException($"Module {name} has a Contracts assembly but no Shop.Modules.{name} assembly."),
                All.SingleOrDefault(assembly => Name(assembly) == $"Shop.Modules.{name}.Contracts")
                    ?? throw new InvalidOperationException($"Module {name} has no Shop.Modules.{name}.Contracts assembly."))),
    ];

    public static IReadOnlyList<Assembly> ModuleImplementations { get; } = [.. Modules.Select(module => module.Implementation)];

    // Loaded once: ArchUnitNET reads every assembly's IL, which is the slow part of these tests
    public static Architecture Architecture { get; } = new ArchLoader().LoadAssemblies([.. All]).Build();

    public static ShopModule Module(string name) => Modules.Single(module => module.Name == name);

    public static string Name(Assembly assembly) => assembly.GetName().Name!;

    // Without this, a failed load surfaces as a TypeInitializationException on every test
    private static Assembly Load(string name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not load {name}, listed in the test's deps.json; rebuild the solution.", exception);
        }
    }

    [GeneratedRegex(@"^Shop\.Modules\.(?<module>[A-Za-z0-9]+)(\.Contracts)?$")]
    private static partial Regex ModuleAssemblyName();
}
