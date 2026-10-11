using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Laundry.Api.Orders;

// Model/migration generation does not require credentials or a running database.
// Explicit application of a migration must supply its connection via EF's --connection option.
public sealed class OrderDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public OrderDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql("Host=localhost;Database=laundry;Username=migration_design_only",
                options => options.MigrationsHistoryTable("__order_migrations_history", "orders"))
            .Options);
}
