using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Modules.Orders.Domain;

namespace Shop.Modules.Orders.Data.Configurations;

internal sealed class OrderStatusChangeConfiguration : IEntityTypeConfiguration<OrderStatusChange>
{
    public void Configure(EntityTypeBuilder<OrderStatusChange> builder)
    {
        builder.ToTable("order_status_history");
        builder.HasKey(change => change.Id);

        builder.Property(change => change.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(change => change.OrderId).HasColumnName("order_id");
        builder.Property(change => change.FromStatus).HasColumnName("from_status").HasConversion<string>().HasMaxLength(20);
        builder.Property(change => change.ToStatus).HasColumnName("to_status").HasConversion<string>().HasMaxLength(20);
        builder.Property(change => change.ChangedBy).HasColumnName("changed_by");
        builder.Property(change => change.ChangedAt).HasColumnName("changed_at");

        builder.HasIndex(change => new { change.OrderId, change.ChangedAt });
    }
}
