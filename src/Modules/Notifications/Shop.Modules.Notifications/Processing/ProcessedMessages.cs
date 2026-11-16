using Microsoft.EntityFrameworkCore;
using Shop.Modules.Notifications.Data;

namespace Shop.Modules.Notifications.Processing;

/// <summary>
/// The <c>notifications.processed_messages</c> table: which messages are done. RabbitMQ delivers at least once, so the
/// same message can arrive again after a crash or a resend from the outbox; this is how the worker notices.
/// </summary>
internal sealed class ProcessedMessages(NotificationsDbContext db, TimeProvider time)
{
    public Task<bool> ContainsAsync(Guid messageId, CancellationToken cancellationToken) =>
        db.ProcessedMessages.AnyAsync(message => message.MessageId == messageId, cancellationToken);

    /// <summary>Records a message as done. Recording it twice is harmless, as two workers might.</summary>
    public async Task RecordAsync(Guid messageId, string type, CancellationToken cancellationToken)
    {
        var processedAt = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO notifications.processed_messages (message_id, type, processed_at)
            VALUES ({messageId}, {type}, {processedAt})
            ON CONFLICT (message_id) DO NOTHING
            """, cancellationToken);
    }
}
