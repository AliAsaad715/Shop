namespace Shop.SharedKernel.Persistence;

// Recorded by AddModuleDbContext so startup can migrate every module without knowing the modules
internal sealed record ModuleDbContextRegistration(Type ContextType);
