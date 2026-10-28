using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Modules.Orders.Domain;

namespace Shop.Modules.Orders.Data.Configurations;

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("order_lines", table =>
        {
            table.HasCheckConstraint("ck_order_lines_quantity_positive", "quantity > 0");
            table.HasCheckConstraint("ck_order_lines_unit_price_nonnegative", "unit_price >= 0");
        });
        builder.HasKey(line => line.Id);

        builder.Property(line => line.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(line => line.OrderId).HasColumnName("order_id");

        // No foreign key to products.products: Orders never reaches into another module's schema
        builder.Property(line => line.ProductId).HasColumnName("product_id");
        builder.Property(line => line.ProductName).HasColumnName("product_name").HasMaxLength(200).IsRequired();
        builder.Property(line => line.UnitPrice).HasColumnName("unit_price").HasPrecision(18, 2);
        builder.Property(line => line.Quantity).HasColumnName("quantity");
        builder.Property(line => line.LineTotal).HasColumnName("line_total").HasPrecision(28, 2);

        builder.HasIndex(line => new { line.OrderId, line.ProductId }).IsUnique();
    }
}
