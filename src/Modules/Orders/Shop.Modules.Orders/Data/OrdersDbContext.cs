using Microsoft.EntityFrameworkCore;
using Shop.Modules.Orders.Domain;

namespace Shop.Modules.Orders.Data;

internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    internal const string Schema = "orders";

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<OrderStatusChange> OrderStatusHistory => Set<OrderStatusChange>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrdersDbContext).Assembly);
    }
}
