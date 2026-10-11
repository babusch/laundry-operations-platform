using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Orders;

public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<ServiceOrder> Orders => Set<ServiceOrder>();
    public DbSet<OrderAuditEntry> AuditEntries => Set<OrderAuditEntry>();
    public DbSet<OrderCommandReceipt> CommandReceipts => Set<OrderCommandReceipt>();
    public DbSet<OrderNumberCounter> NumberCounters => Set<OrderNumberCounter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<ServiceOrder>();
        order.ToTable("service_orders", "orders", table =>
        {
            table.HasCheckConstraint("ck_order_dates", "planned_delivery_date >= planned_receiving_date");
            table.HasCheckConstraint("ck_order_status", "status = 'planned'");
            table.HasCheckConstraint("ck_order_version", "version > 0");
        });
        order.HasKey(x => x.Id);
        order.HasAlternateKey(x => new { x.TenantId, x.PlantId, x.Id });
        order.HasIndex(x => new { x.TenantId, x.PlantId, x.OrderNumber }).IsUnique();
        order.HasIndex(x => new { x.TenantId, x.PlantId, x.PlannedReceivingDate, x.Id });
        order.HasIndex(x => new { x.TenantId, x.PlantId, x.CustomerId, x.PlannedReceivingDate, x.Id });
        order.Property(x => x.CustomerReference).HasMaxLength(100);
        order.Property(x => x.Notes).HasMaxLength(2000);
        order.Property(x => x.Version).IsConcurrencyToken();
        order.Property(x => x.Status).IsConcurrencyToken();

        var audit = modelBuilder.Entity<OrderAuditEntry>();
        audit.ToTable("order_audit_entries", "orders");
        audit.HasKey(x => x.Id);
        audit.HasIndex(x => new { x.TenantId, x.PlantId, x.OrderId, x.OrderVersion }).IsUnique();
        audit.HasOne<ServiceOrder>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.PlantId, x.OrderId })
            .HasPrincipalKey(x => new { x.TenantId, x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        audit.Property(x => x.BeforePlan).HasColumnType("jsonb");
        audit.Property(x => x.AfterPlan).HasColumnType("jsonb");

        var receipt = modelBuilder.Entity<OrderCommandReceipt>();
        receipt.ToTable("command_receipts", "orders");
        receipt.HasKey(x => new { x.TenantId, x.PlantId, x.ActorIssuer, x.ActorSubject, x.IdempotencyKey });
        receipt.HasOne<ServiceOrder>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.PlantId, x.OrderId })
            .HasPrincipalKey(x => new { x.TenantId, x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        receipt.Property(x => x.ResponseBody).HasColumnType("jsonb");

        var counter = modelBuilder.Entity<OrderNumberCounter>();
        counter.ToTable("number_counters", "orders");
        counter.HasKey(x => new { x.TenantId, x.PlantId });

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
            }
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ChangeTracker.Entries().Any(x =>
                (x.Entity is OrderAuditEntry or OrderCommandReceipt) &&
                (x.State is EntityState.Modified or EntityState.Deleted)))
        {
            throw new InvalidOperationException("Order history and successful receipts are append-only.");
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
