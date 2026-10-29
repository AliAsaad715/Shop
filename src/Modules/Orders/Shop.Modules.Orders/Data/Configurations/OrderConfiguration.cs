using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Modules.Orders.Domain;

namespace Shop.Modules.Orders.Data.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", table => table.HasCheckConstraint("ck_orders_total_nonnegative", "total >= 0"));
        builder.HasKey(order => order.Id);

        builder.Property(order => order.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(order => order.CustomerId).HasColumnName("customer_id");
        builder.Property(order => order.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);

        // Wider than a unit price: up to 50 lines of up to 1000 units each
        builder.Property(order => order.Total).HasColumnName("total").HasPrecision(28, 2);
        builder.Property(order => order.CreatedAt).HasColumnName("created_at");
        builder.Property(order => order.UpdatedAt).HasColumnName("updated_at");
        builder.Property(order => order.Version).HasColumnName("version").IsConcurrencyToken();

        builder.HasMany(order => order.Lines)
            .WithOne()
            .HasForeignKey(line => line.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(order => order.History)
            .WithOne()
            .HasForeignKey(change => change.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // A customer's orders newest first, and the admin list filtered by status; the ID breaks ties
        builder.HasIndex(order => new { order.CustomerId, order.CreatedAt, order.Id }).IsDescending(false, true, true);
        builder.HasIndex(order => new { order.Status, order.CreatedAt, order.Id }).IsDescending(false, true, true);
        builder.HasIndex(order => new { order.CreatedAt, order.Id }).IsDescending(true, true);
    }
}
