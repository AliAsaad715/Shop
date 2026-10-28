using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Shop.ArchitectureTests;

/// <summary>
/// Finds a module's EF Core types by reflection, so new contexts, configurations and entities are covered automatically.
/// </summary>
internal static class PersistenceTypes
{
    public static IReadOnlyList<Type> DbContexts(Assembly assembly) =>
        [.. AllTypes(assembly).Where(type => !type.IsAbstract && typeof(DbContext).IsAssignableFrom(type))];

    public static IReadOnlyList<Type> EntityTypeConfigurations(Assembly assembly) =>
    [
        .. AllTypes(assembly).Where(type => type.GetInterfaces().Any(contract =>
            contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>))),
    ];

    /// <summary>
    /// The entity types each DbContext maps, read from its built model, plus everything in a Domain namespace.
    /// </summary>
    public static IReadOnlyList<Type> DomainEntities(Assembly assembly, string moduleNamespace)
    {
        var mapped = DbContexts(assembly)
            .SelectMany(MappedClrTypes)
            .Where(type => type.Assembly.GetName().Name!.StartsWith("Shop.", StringComparison.Ordinal));

        var domainNamespace = $"{moduleNamespace}.Domain";
        var inDomainNamespace = AllTypes(assembly).Where(type =>
            type.Namespace == domainNamespace || type.Namespace?.StartsWith($"{domainNamespace}.", StringComparison.Ordinal) == true);

        return [.. mapped.Concat(inDomainNamespace).Distinct()];
    }

    /// <summary>
    /// Every database object the context maps, with its schema. A SQL-query mapping has no schema we can check,
    /// so it is listed with a null schema and never matches.
    /// </summary>
    public static IReadOnlyList<(string StoreObject, string? Schema)> MappedStoreObjects(Type contextType)
    {
        var model = DesignTimeModel(contextType);
        var storeObjects = new List<(string StoreObject, string? Schema)> { ("default schema", model.GetDefaultSchema()) };

        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.GetTableName() is { } table)
            {
                storeObjects.Add(($"table {table} ({entityType.DisplayName()})", entityType.GetSchema()));
            }

            if (entityType.GetViewName() is { } view)
            {
                storeObjects.Add(($"view {view} ({entityType.DisplayName()})", entityType.GetViewSchema()));
            }

            if (entityType.GetSqlQuery() is not null)
            {
                storeObjects.Add(($"SQL query ({entityType.DisplayName()})", null));
            }
        }

        storeObjects.AddRange(model.GetSequences().Select(sequence => ($"sequence {sequence.Name}", sequence.Schema)));
        storeObjects.AddRange(model.GetDbFunctions()
            .Where(function => !function.IsBuiltIn)
            .Select(function => ($"function {function.Name}", function.Schema)));

        return storeObjects;
    }

    private static IEnumerable<Type> MappedClrTypes(Type contextType) =>
        DesignTimeModel(contextType).GetEntityTypes().Select(entityType => entityType.ClrType);

    // The design-time model keeps the relational annotations (schemas, views, sequences) the runtime model may drop
    private static IModel DesignTimeModel(Type contextType)
    {
        // Building the model needs a provider but never opens a connection
        var optionsBuilder = (DbContextOptionsBuilder)Activator.CreateInstance(
            typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
        optionsBuilder.UseNpgsql("Host=unused");

        using var context = (DbContext)Activator.CreateInstance(contextType, optionsBuilder.Options)!;

        return context.GetService<IDesignTimeModel>().Model;
    }

    // Compiler-generated types (closures, state machines) aren't part of the module's surface
    private static IEnumerable<Type> AllTypes(Assembly assembly) => assembly
        .GetTypes()
        .Where(type => type.GetCustomAttribute<CompilerGeneratedAttribute>() is null);
}
