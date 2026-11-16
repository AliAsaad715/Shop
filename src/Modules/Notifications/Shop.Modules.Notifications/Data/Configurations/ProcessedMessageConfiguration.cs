using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Modules.Notifications.Processing;

namespace Shop.Modules.Notifications.Data.Configurations;

internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages");

        // Keyed by message ID, so the check for a repeat is one index lookup
        builder.HasKey(message => message.MessageId);
        builder.Property(message => message.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        builder.Property(message => message.Type).HasColumnName("type").HasMaxLength(ProcessedMessage.MaxTypeLength);
        builder.Property(message => message.ProcessedAt).HasColumnName("processed_at");
    }
}
