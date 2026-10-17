using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Orders;

public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<ServiceOrder> Orders => Set<ServiceOrder>();
    public DbSet<OrderAuditEntry> AuditEntries => Set<OrderAuditEntry>();
    public DbSet<OrderCommandReceipt> CommandReceipts => Set<OrderCommandReceipt>();
    public DbSet<OrderNumberCounter> NumberCounters => Set<OrderNumberCounter>();
    public DbSet<OrderNotesDefault> NotesDefaults => Set<OrderNotesDefault>();
    public DbSet<OrderNotesDefaultCommand> NotesDefaultCommands => Set<OrderNotesDefaultCommand>();
    public DbSet<OrderPublicationHead> PublicationHeads => Set<OrderPublicationHead>();
    public DbSet<OrderPublicationFeedState> PublicationFeedStates => Set<OrderPublicationFeedState>();
    public DbSet<OrderPublicationOutboxEntry> PublicationOutbox => Set<OrderPublicationOutboxEntry>();

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
        audit.Property(x => x.ActorDisplayName).HasMaxLength(200);

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

        var defaults = modelBuilder.Entity<OrderNotesDefault>();
        defaults.ToTable("note_defaults", "orders", table => table.HasCheckConstraint("ck_note_default_version", "version > 0"));
        defaults.HasKey(x => new { x.TenantId, x.PlantId });
        defaults.Property(x => x.Version).IsConcurrencyToken();
        var command = modelBuilder.Entity<OrderNotesDefaultCommand>();
        command.ToTable("note_default_commands", "orders", table =>
        {
            table.HasCheckConstraint("ck_note_default_command_versions", "expected_version >= 0 AND result_version > 0");
        });
        command.HasKey(x => new { x.TenantId, x.PlantId, x.ActorIssuer, x.ActorSubject, x.IdempotencyKey });
        command.HasOne<OrderNotesDefault>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlantId }).OnDelete(DeleteBehavior.Restrict);

        var head = modelBuilder.Entity<OrderPublicationHead>();
        head.ToTable("publication_heads", "orders", table => table.HasCheckConstraint("ck_publication_head_version", "version BETWEEN 1 AND 9007199254740991"));
        head.HasKey(x => new { x.TenantId, x.PlantId, x.OrderId });
        head.HasOne<ServiceOrder>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlantId, x.OrderId })
            .HasPrincipalKey(x => new { x.TenantId, x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        head.Property(x => x.Version).IsConcurrencyToken();
        head.Property(x => x.ContentFingerprint).HasMaxLength(64);
        // Text preserves the exact immutable bytes for later retries; never regenerate from a head.
        head.Property(x => x.Payload).HasColumnType("text");
        head.HasIndex(x => new { x.TenantId, x.PlantId, x.ChangePosition }).IsUnique();
        head.ToTable("publication_heads", "orders", table => table.HasCheckConstraint("ck_publication_head_position", "change_position > 0"));
        var feed = modelBuilder.Entity<OrderPublicationFeedState>();
        feed.ToTable("publication_feed_states", "orders", table =>
        {
            table.HasCheckConstraint("ck_publication_feed_position", "last_position > 0");
            table.HasCheckConstraint("ck_publication_feed_epoch", "epoch <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        feed.HasKey(x => new { x.TenantId, x.PlantId });
        var publication = modelBuilder.Entity<OrderPublicationOutboxEntry>();
        publication.ToTable("publication_outbox", "orders", table => table.HasCheckConstraint("ck_publication_outbox_version", "version BETWEEN 1 AND 9007199254740991"));
        publication.HasKey(x => x.Id);
        publication.HasIndex(x => new { x.TenantId, x.PlantId, x.OrderId, x.Version }).IsUnique();
        publication.HasOne<ServiceOrder>().WithMany().HasForeignKey(x => new { x.TenantId, x.PlantId, x.OrderId })
            .HasPrincipalKey(x => new { x.TenantId, x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        publication.Property(x => x.Payload).HasColumnType("text");
        publication.Property(x => x.InitialPublicationActor).HasMaxLength(200);

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
                (x.Entity is OrderAuditEntry or OrderCommandReceipt or OrderNotesDefaultCommand or OrderPublicationOutboxEntry) &&
                (x.State is EntityState.Modified or EntityState.Deleted)))
        {
            throw new InvalidOperationException("Order history and successful receipts are append-only.");
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
