namespace Shop.SharedKernel.Persistence;

// Recorded by AddModuleDbContext so the migrator can initialize every schema without knowing the modules
internal sealed record ModuleDbContextRegistration(Type ContextType);
