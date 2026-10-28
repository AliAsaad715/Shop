using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Modules.Users.Domain;
using Shop.SharedKernel.Auth;

namespace Shop.Modules.Users.Data.Configurations;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        // Fixed IDs and stamps keep the seed identical between migrations
        builder.HasData(
            new Role
            {
                Id = Guid.Parse("019a2180-0000-7000-8000-000000000101"),
                Name = Roles.Customer,
                NormalizedName = Roles.Customer.ToUpperInvariant(),
                ConcurrencyStamp = "019a2180-0000-7000-8000-000000000101",
            },
            new Role
            {
                Id = Guid.Parse("019a2180-0000-7000-8000-000000000102"),
                Name = Roles.Admin,
                NormalizedName = Roles.Admin.ToUpperInvariant(),
                ConcurrencyStamp = "019a2180-0000-7000-8000-000000000102",
            });
    }
}
