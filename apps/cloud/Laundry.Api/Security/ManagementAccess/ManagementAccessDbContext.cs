using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Laundry.Api.Security.ManagementAccess;

public sealed class PlantMembership
{
    public Guid Id { get; set; }
    public string ActorIssuer { get; set; } = "";
    public string ActorSubject { get; set; } = "";
    public Guid TenantId { get; set; }
    public Guid PlantId { get; set; }
    public bool IsActive { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public List<PlantMembershipPermission> Permissions { get; set; } = [];
}

public sealed class PlantMembershipPermission
{
    public Guid MembershipId { get; set; }
    public string Permission { get; set; } = "";
}

public sealed class MembershipAuditEntry
{
    public Guid Id { get; set; }
    public Guid MembershipId { get; set; }
    public long MembershipVersion { get; set; }
    public string Action { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string RecordedBy { get; set; } = "";
    public string AfterState { get; set; } = "";
}

public sealed class ManagementAccessDbContext(DbContextOptions<ManagementAccessDbContext> options) : DbContext(options)
{
    public DbSet<PlantMembership> Memberships => Set<PlantMembership>();
    public DbSet<PlantMembershipPermission> Permissions => Set<PlantMembershipPermission>();
    public DbSet<MembershipAuditEntry> AuditEntries => Set<MembershipAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var member = modelBuilder.Entity<PlantMembership>();
        member.ToTable("plant_memberships", "identity_access", table =>
        {
            table.HasCheckConstraint("ck_membership_scope", "tenant_id <> '00000000-0000-0000-0000-000000000000'::uuid AND plant_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_membership_identity", "length(btrim(actor_issuer)) > 0 AND length(btrim(actor_subject)) > 0");
            table.HasCheckConstraint("ck_membership_version", "version > 0");
        });
        member.HasKey(x => x.Id);
        member.HasIndex(x => new { x.ActorIssuer, x.ActorSubject, x.TenantId, x.PlantId }).IsUnique();
        member.Property(x => x.Version).IsConcurrencyToken();
        member.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Restrict);

        var permission = modelBuilder.Entity<PlantMembershipPermission>();
        permission.ToTable("plant_membership_permissions", "identity_access", table =>
            table.HasCheckConstraint("ck_membership_permission", "permission IN ('orders.read', 'orders.create', 'orders.plan.edit', 'orders.notes.default.edit')"));
        permission.HasKey(x => new { x.MembershipId, x.Permission });

        var audit = modelBuilder.Entity<MembershipAuditEntry>();
        audit.ToTable("membership_audit_entries", "identity_access");
        audit.HasKey(x => x.Id);
        audit.HasIndex(x => new { x.MembershipId, x.MembershipVersion }).IsUnique();
        audit.HasOne<PlantMembership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Restrict);
        audit.Property(x => x.AfterState).HasColumnType("jsonb");

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ChangeTracker.Entries<MembershipAuditEntry>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Membership history is append-only.");
        return base.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ManagementAccessDbContextFactory : IDesignTimeDbContextFactory<ManagementAccessDbContext>
{
    public ManagementAccessDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<ManagementAccessDbContext>()
            .UseNpgsql("Host=localhost;Database=laundry;Username=migration_design_only",
                options => options.MigrationsHistoryTable("__management_access_migrations", "identity_access")).Options);
}
