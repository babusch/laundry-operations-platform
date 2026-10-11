using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Laundry.Api.Customers;

public sealed class LaundryCustomer
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsActive { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class CustomerPlantService
{
    public Guid TenantId { get; set; }
    public Guid PlantId { get; set; }
    public Guid CustomerId { get; set; }
    public bool IsActive { get; set; }
}

public sealed class CustomerSetupAudit
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public string Action { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string RecordedBy { get; set; } = "";
    public string AfterState { get; set; } = "";
}

public sealed class CustomerDbContext(DbContextOptions<CustomerDbContext> options) : DbContext(options)
{
    public DbSet<LaundryCustomer> Customers => Set<LaundryCustomer>();
    public DbSet<CustomerPlantService> PlantServices => Set<CustomerPlantService>();
    public DbSet<CustomerSetupAudit> AuditEntries => Set<CustomerSetupAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var customer = modelBuilder.Entity<LaundryCustomer>();
        customer.ToTable("laundry_customers", "customers", table =>
        {
            table.HasCheckConstraint("ck_customer_scope", "tenant_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_customer_labels", "length(btrim(code)) > 0 AND length(btrim(display_name)) > 0");
            table.HasCheckConstraint("ck_customer_version", "version > 0");
        });
        customer.HasKey(x => x.Id);
        customer.HasAlternateKey(x => new { x.TenantId, x.Id });
        customer.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        customer.Property(x => x.Code).HasMaxLength(50);
        customer.Property(x => x.DisplayName).HasMaxLength(200);
        customer.Property(x => x.Version).IsConcurrencyToken();

        var service = modelBuilder.Entity<CustomerPlantService>();
        service.ToTable("plant_services", "customers", table =>
            table.HasCheckConstraint("ck_customer_service_plant", "plant_id <> '00000000-0000-0000-0000-000000000000'::uuid"));
        service.HasKey(x => new { x.TenantId, x.PlantId, x.CustomerId });
        service.HasOne<LaundryCustomer>().WithMany().HasForeignKey(x => new { x.TenantId, x.CustomerId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var audit = modelBuilder.Entity<CustomerSetupAudit>();
        audit.ToTable("setup_audit_entries", "customers");
        audit.HasKey(x => x.Id);
        audit.HasOne<LaundryCustomer>().WithMany().HasForeignKey(x => new { x.TenantId, x.CustomerId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        audit.Property(x => x.AfterState).HasColumnType("jsonb");
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ChangeTracker.Entries<CustomerSetupAudit>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Customer setup history is append-only.");
        return base.SaveChangesAsync(cancellationToken);
    }
}

public sealed class CustomerDbContextFactory : IDesignTimeDbContextFactory<CustomerDbContext>
{
    public CustomerDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<CustomerDbContext>()
            .UseNpgsql("Host=localhost;Database=laundry;Username=migration_design_only",
                provider => provider.MigrationsHistoryTable("__customer_migrations", "customers")).Options);
}
