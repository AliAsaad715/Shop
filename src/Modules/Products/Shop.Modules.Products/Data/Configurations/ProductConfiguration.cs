using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Modules.Products.Domain;

namespace Shop.Modules.Products.Data.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", table => table.HasCheckConstraint("ck_products_price_nonnegative", "price >= 0"));
        builder.HasKey(product => product.Id);

        builder.Property(product => product.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(product => product.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(product => product.Description).HasColumnName("description").HasMaxLength(2000).IsRequired();
        builder.Property(product => product.Price).HasColumnName("price").HasPrecision(18, 2);
        builder.Property(product => product.ImagePath).HasColumnName("image_path").HasMaxLength(512).IsRequired();
        builder.Property(product => product.CreatedAt).HasColumnName("created_at");
        builder.Property(product => product.CategoryId).HasColumnName("category_id");

        builder.HasOne(product => product.Category)
            .WithMany()
            .HasForeignKey(product => product.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // The ID breaks ties when several products have the same creation time.
        builder.HasIndex(product => new { product.CreatedAt, product.Id }).IsDescending(true, false);
        builder.HasIndex(product => new { product.CategoryId, product.CreatedAt, product.Id }).IsDescending(false, true, false);
    }
}
