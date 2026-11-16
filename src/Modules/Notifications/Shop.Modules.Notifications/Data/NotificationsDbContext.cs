using Microsoft.EntityFrameworkCore;
using Shop.Modules.Notifications.Processing;

namespace Shop.Modules.Notifications.Data;

/// <summary>
/// The Notifications module's own tables, in the <c>notifications</c> schema. Only the email worker uses it, and it
/// never reads another module's tables: everything an email needs comes in the message.
/// </summary>
internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    internal const string Schema = "notifications";

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);
    }
}
