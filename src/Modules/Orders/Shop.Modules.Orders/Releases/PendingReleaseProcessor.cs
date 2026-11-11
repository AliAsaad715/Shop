using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shop.Modules.Orders.Data;
using Shop.Modules.Products.Contracts;

namespace Shop.Modules.Orders.Releases;

internal sealed class PendingReleaseProcessor(
    OrdersDbContext db, IInventoryService inventory, TimeProvider time, ILogger<PendingReleaseProcessor> logger)
{
    public async Task ProcessDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = await db.PendingReleases.Where(p => p.NextAttemptAt <= now)
            .OrderBy(p => p.NextAttemptAt).ThenBy(p => p.OrderId).Take(100).ToListAsync(ct);
        foreach (var pending in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Warehouse release is idempotent, so a crash after this call is safe to retry.
                await inventory.ReleaseAsync(pending.OrderId, ct);
                db.PendingReleases.Remove(pending);
            }
            catch (InventoryException error)
            {
                pending.RetryFailed(error.Message, time.GetUtcNow());
                logger.LogWarning(error, "Retry {Attempt} failed for warehouse release {OrderId}", pending.Attempts, pending.OrderId);
            }
            // Persist each result independently: a later failure must not lose completed releases.
            await db.SaveChangesAsync(ct);
        }
    }
}
