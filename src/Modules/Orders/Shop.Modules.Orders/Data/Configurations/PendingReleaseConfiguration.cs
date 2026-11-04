using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Modules.Orders.Releases;

namespace Shop.Modules.Orders.Data.Configurations;

internal sealed class PendingReleaseConfiguration : IEntityTypeConfiguration<PendingRelease>
{
    public void Configure(EntityTypeBuilder<PendingRelease> builder)
    {
        // No foreign key to orders: a reservation that timed out belongs to an order that was never saved
        builder.ToTable("pending_releases");
        builder.HasKey(pending => pending.OrderId);

        builder.Property(pending => pending.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(pending => pending.QueuedAt).HasColumnName("queued_at");
        builder.Property(pending => pending.Attempts).HasColumnName("attempts");
        builder.Property(pending => pending.NextAttemptAt).HasColumnName("next_attempt_at");
        builder.Property(pending => pending.LastError).HasColumnName("last_error").HasMaxLength(PendingRelease.MaxErrorLength);

        // The worker reads the rows that are due, oldest first
        builder.HasIndex(pending => pending.NextAttemptAt);
    }
}
