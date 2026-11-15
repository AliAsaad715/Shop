using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Modules.Orders.Outbox;

namespace Shop.Modules.Orders.Data.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public const string UnpublishedIndex = "IX_outbox_messages_unpublished";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        // No foreign key to orders: a row is a message about an order, not part of it
        builder.ToTable("outbox_messages");
        builder.HasKey(message => message.Id);

        builder.Property(message => message.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(message => message.Type).HasColumnName("type").HasMaxLength(OutboxMessage.MaxTypeLength);
        builder.Property(message => message.Payload).HasColumnName("payload").HasColumnType("jsonb");
        builder.Property(message => message.OccurredAt).HasColumnName("occurred_at");
        builder.Property(message => message.PublishedAt).HasColumnName("published_at");
        builder.Property(message => message.Attempts).HasColumnName("attempts");
        builder.Property(message => message.LastError).HasColumnName("last_error").HasMaxLength(OutboxMessage.MaxErrorLength);

        // The relay reads unpublished rows oldest first. Only those rows are in this index, so it stays small however
        // many published rows the table keeps
        builder.HasIndex(message => new { message.OccurredAt, message.Id })
            .HasDatabaseName(UnpublishedIndex)
            .HasFilter("published_at IS NULL");
    }
}
