using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shop.Modules.Orders.Data;
using Shop.Modules.Products.Contracts;

namespace Shop.Modules.Orders.Releases;

internal sealed class ReservationReleaser(
    IInventoryService inventory, IServiceScopeFactory scopes, TimeProvider time, ILogger<ReservationReleaser> logger)
{
    public async Task ReleaseAsync(Guid orderId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await inventory.ReleaseAsync(orderId, timeout.Token);
        }
        catch (Exception error) when (error is InventoryException || error is OperationCanceledException && timeout.IsCancellationRequested)
        {
            // A failed order save can leave Added entities in its DbContext. Queue in an independent
            // scope so releasing a reservation can never accidentally retry saving that order.
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            var pending = new PendingRelease(orderId, error.Message, time.GetUtcNow());
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO orders.pending_releases (order_id, queued_at, attempts, next_attempt_at, last_error)
                VALUES ({pending.OrderId}, {pending.QueuedAt}, {pending.Attempts}, {pending.NextAttemptAt}, {pending.LastError})
                ON CONFLICT (order_id) DO NOTHING
                """, CancellationToken.None);
            logger.LogWarning(error, "Queued warehouse release for order {OrderId}", orderId);
        }
    }
}
