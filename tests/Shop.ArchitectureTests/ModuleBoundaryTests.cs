using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Shop.ArchitectureTests;

/// <summary>
/// Evidence for the plan's "no module reads another module's tables" rule: modules talk only through Contracts,
/// each module's DbContext and entities stay inside its own assembly, and its DbContext maps only its own schema.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly Architecture _architecture = ShopAssemblies.Architecture;

    public static TheoryData<string> Modules => [.. ShopAssemblies.Modules.Select(module => module.Name)];

    public static TheoryData<string> ModulesWithDbContexts =>
    [
        .. ShopAssemblies.Modules
            .Where(module => PersistenceTypes.DbContexts(module.Implementation).Count > 0)
            .Select(module => module.Name),
    ];

    [Theory]
    [MemberData(nameof(Modules))]
    public void ModuleDoesNotDependOnAnotherModulesImplementation(string moduleName)
    {
        var module = ShopAssemblies.Module(moduleName);
        var otherImplementations = ShopAssemblies.ModuleImplementations
            .Where(assembly => assembly != module.Implementation)
            .ToArray();

        if (otherImplementations.Length == 0)
        {
            return;
        }

        IArchRule rule = Types().That().ResideInAssembly(module.Implementation)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(otherImplementations[0], otherImplementations[1..]))
            .Because($"{module.Name} may use other modules only through their .Contracts assemblies");

        rule.Check(_architecture);
    }

    [Theory]
    [MemberData(nameof(ModulesWithDbContexts))]
    public void ModuleDbContextIsUsedOnlyInsideItsModule(string moduleName)
    {
        var module = ShopAssemblies.Module(moduleName);
        var dbContexts = PersistenceTypes.DbContexts(module.Implementation);

        IArchRule rule = Types().That().DoNotResideInAssembly(module.Implementation)
            .Should().NotDependOnAny(Types().That().Are(dbContexts))
            .Because($"only {module.Name} may query its own tables");

        rule.Check(_architecture);
    }

    [Theory]
    [MemberData(nameof(ModulesWithDbContexts))]
    public void ModuleDbContextMapsOnlyItsOwnSchema(string moduleName)
    {
        var module = ShopAssemblies.Module(moduleName);

        // One schema per module, named after it, so no two modules' contexts can map the same table.
        // Raw SQL (FromSql, ExecuteSql, Dapper) against another schema can't be seen statically; review covers that.
        var expectedSchema = module.Name.ToLowerInvariant();
        var foreign = PersistenceTypes.DbContexts(module.Implementation)
            .SelectMany(context => PersistenceTypes.MappedStoreObjects(context)
                .Where(mapped => mapped.Schema != expectedSchema)
                .Select(mapped => $"{context.Name}: {mapped.StoreObject} is in schema '{mapped.Schema ?? "(none)"}'"))
            .ToList();

        Assert.True(
            foreign.Count == 0,
            $"Only {module.Name} may map tables, and only in schema '{expectedSchema}': " + string.Join("; ", foreign));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void ModulePersistenceTypesAreInternal(string moduleName)
    {
        var module = ShopAssemblies.Module(moduleName);
        var persistenceTypes = PersistenceTypes.DbContexts(module.Implementation)
            .Concat(PersistenceTypes.EntityTypeConfigurations(module.Implementation))
            .Concat(PersistenceTypes.DomainEntities(module.Implementation, ShopAssemblies.Name(module.Implementation)))
            .Distinct()
            .ToList();

        // A module without storage (Notifications today) has nothing to hide
        if (persistenceTypes.Count == 0)
        {
            return;
        }

        IArchRule rule = Types().That().Are(persistenceTypes)
            .Should().NotBePublic()
            .Because("DbContexts, EF configurations and domain entities are module internals other assemblies must not reach");

        rule.Check(_architecture);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void ModuleExposesOnlyItsEntryPoint(string moduleName)
    {
        var module = ShopAssemblies.Module(moduleName);

        // The static XModule class is how the host wires a module; EF generates migrations as public, harmlessly
        var unexpected = module.Implementation.GetExportedTypes()
            .Where(type => !(type.IsAbstract && type.IsSealed && type.Name == $"{module.Name}Module"))
            .Where(type => !typeof(Migration).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Only the static {module.Name}Module class may be public in {ShopAssemblies.Name(module.Implementation)}; "
                + "make these internal or move them to Contracts: " + string.Join(", ", unexpected));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void ContractsDoNotDependOnModuleImplementationsOrEfCore(string moduleName)
    {
        var module = ShopAssemblies.Module(moduleName);
        var implementations = ShopAssemblies.ModuleImplementations.ToArray();

        // A module whose Contracts are still empty has nothing to check yet, hence no positive results required
        IArchRule rule = Types().That().ResideInAssembly(module.Contracts)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(implementations[0], implementations[1..]))
            .AndShould().NotDependOnAny(Types(true).That().ResideInNamespaceMatching(@"^(Microsoft\.EntityFrameworkCore|Npgsql|System\.Data)(\..+)?$"))
            .Because("Contracts hold only public interfaces and DTOs, so referencing them never exposes a module's internals or storage "
                + "(EF Core, Npgsql or ADO.NET types)")
            .WithoutRequiringPositiveResults();

        rule.Check(_architecture);
    }

    [Fact]
    public void SharedKernelDoesNotDependOnAnyModule()
    {
        var moduleAssemblies = ShopAssemblies.Modules
            .SelectMany(module => new[] { module.Implementation, module.Contracts })
            .ToArray();

        IArchRule rule = Types().That().ResideInAssembly(ShopAssemblies.SharedKernel)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(moduleAssemblies[0], moduleAssemblies[1..]))
            .Because("every module depends on SharedKernel, so it must not know about any of them");

        rule.Check(_architecture);
    }
}
