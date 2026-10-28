using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Modules.Products.Domain;

namespace Shop.Modules.Products.Data.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");
        builder.HasKey(category => category.Id);

        builder.Property(category => category.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(category => category.Name).HasColumnName("name").HasMaxLength(100).IsRequired();

        builder.HasIndex(category => category.Name).IsUnique();

        // Fixed IDs keep migration seed data stable and usable by every environment.
        builder.HasData(
            new Category { Id = Guid.Parse("019a2180-0000-7000-8000-000000000001"), Name = "Electronics" },
            new Category { Id = Guid.Parse("019a2180-0000-7000-8000-000000000002"), Name = "Clothing" },
            new Category { Id = Guid.Parse("019a2180-0000-7000-8000-000000000003"), Name = "Home" },
            new Category { Id = Guid.Parse("019a2180-0000-7000-8000-000000000004"), Name = "Books" });
    }
}
