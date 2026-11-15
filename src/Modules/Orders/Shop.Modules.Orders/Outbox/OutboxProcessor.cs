using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shop.Messaging.Publishing;
using Shop.Modules.Orders.Data;

namespace Shop.Modules.Orders.Outbox;

/// <summary>What one relay pass did: how many rows it took, and how many of those RabbitMQ confirmed.</summary>
internal readonly record struct OutboxPass(int Taken, int Published)
{
    public int Failed => Taken - Published;
}

/// <summary>
/// One relay pass in one transaction: lock the oldest unpublished rows, publish them, and mark the ones RabbitMQ
/// confirmed. A row it didn't confirm stays unpublished with one more attempt counted, and a later pass retries it.
/// </summary>
internal sealed class OutboxProcessor(OrdersDbContext db, IEventPublisher publisher, IOptions<OutboxOptions> options, TimeProvider time)
{
    private const int DeleteBatchSize = 1000;

    public async Task<OutboxPass> PublishPendingAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // SKIP LOCKED: rows another instance's relay is publishing right now are skipped instead of waited for, so
        // two relays never publish the same row at the same time. The locks last until this transaction ends
        var batch = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM orders.outbox_messages
                WHERE published_at IS NULL
                ORDER BY occurred_at, id
                LIMIT {options.Value.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);
        if (batch.Count == 0)
        {
            return new OutboxPass(0, 0);
        }

        var results = await publisher.PublishAsync(
            [.. batch.Select(row => new OutgoingMessage(row.Id, row.Type, Encoding.UTF8.GetBytes(row.Payload), row.OccurredAt))], ct);
        var outcomes = results.ToDictionary(result => result.MessageId);

        var now = time.GetUtcNow();
        var published = 0;
        foreach (var row in batch)
        {
            if (outcomes.TryGetValue(row.Id, out var outcome) && outcome.Confirmed)
            {
                row.MarkPublished(now);
                published++;
            }
            else
            {
                row.MarkFailed(outcome?.Failure ?? "The publisher returned no result for it.");
            }
        }

        // If the app stops before this commit, the rows stay unpublished and go out again: at least once, never lost
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new OutboxPass(batch.Count, published);
    }

    /// <summary>
    /// Deletes rows published before <paramref name="cutoff"/>, a batch at a time so no single statement runs long.
    /// Unpublished rows always stay.
    /// </summary>
    public async Task<int> DeletePublishedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var deleted = 0;
        while (true)
        {
            var batch = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM orders.outbox_messages
                WHERE id IN (SELECT id FROM orders.outbox_messages WHERE published_at < {cutoff} LIMIT {DeleteBatchSize})
                """, ct);
            deleted += batch;
            if (batch < DeleteBatchSize)
            {
                return deleted;
            }
        }
    }
}
