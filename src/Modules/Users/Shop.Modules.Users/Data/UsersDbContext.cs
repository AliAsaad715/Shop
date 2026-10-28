using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shop.Modules.Users.Domain;

namespace Shop.Modules.Users.Data;

internal sealed class UsersDbContext(DbContextOptions<UsersDbContext> options)
    : IdentityDbContext<User, Role, Guid>(options)
{
    internal const string Schema = "users";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Identity's model first, so the names below replace its AspNet* defaults
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<User>().ToTable("users");

        // Identity checks RequireUniqueEmail in code only; the index stops two concurrent registrations
        modelBuilder.Entity<User>().HasIndex(user => user.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique();

        modelBuilder.Entity<Role>().ToTable("roles");
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UsersDbContext).Assembly);

        // snake_case columns, like the products schema
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entityType => entityType.GetProperties()))
        {
            property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
        }
    }
}
