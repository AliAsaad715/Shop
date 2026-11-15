using Microsoft.EntityFrameworkCore;
using Shop.Modules.Orders.Domain;
using Shop.Modules.Orders.Outbox;
using Shop.Modules.Orders.Releases;

namespace Shop.Modules.Orders.Data;

/// <param name="outboxSignal">Wakes the outbox relay after a save writes events; null where no relay runs.</param>
internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options, OutboxSignal? outboxSignal) : DbContext(options)
{
    internal const string Schema = "orders";

    // For tools that create a context from its options alone, such as the architecture tests. Dependency injection
    // picks the constructor above, the longest one it can fill
    public OrdersDbContext(DbContextOptions<OrdersDbContext> options)
        : this(options, null)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<OrderStatusChange> OrderStatusHistory => Set<OrderStatusChange>();

    public DbSet<PendingRelease> PendingReleases => Set<PendingRelease>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var events = AddOutboxMessages();
        var saved = base.SaveChanges(acceptAllChangesOnSuccess);
        WakeRelay(events);
        return saved;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var events = AddOutboxMessages();
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        WakeRelay(events);
        return saved;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrdersDbContext).Assembly);
    }

    /// <summary>
    /// Adds an outbox row for every event the tracked orders recorded. They go in the same SaveChanges as the order
    /// changes, so one transaction commits both or neither: a saved order always has its event, and a failed save
    /// leaves no event behind. If the save fails the rows stay tracked, so saving this context again keeps them.
    /// </summary>
    private int AddOutboxMessages()
    {
        var messages = ChangeTracker.Entries<Order>()
            .SelectMany(entry => entry.Entity.DequeueEvents())
            .Select(OrderEventMessages.ToOutboxMessage)
            .ToList();
        OutboxMessages.AddRange(messages);
        return messages.Count;
    }

    // Inside a transaction someone else opened, the rows aren't committed yet: the relay finds nothing and picks them
    // up at its next poll instead
    private void WakeRelay(int events)
    {
        if (events > 0)
        {
            outboxSignal?.Notify();
        }
    }
}
