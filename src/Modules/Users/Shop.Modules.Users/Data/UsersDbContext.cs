using Microsoft.EntityFrameworkCore;

namespace Shop.Modules.Users.Data;

internal sealed class UsersDbContext(DbContextOptions<UsersDbContext> options) : DbContext(options)
{
    internal const string Schema = "users";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UsersDbContext).Assembly);
    }
}
