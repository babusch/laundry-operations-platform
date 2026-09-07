using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Integrations.Scans;

public sealed class ScanDbContext(DbContextOptions<ScanDbContext> options) : DbContext(options)
{
    public DbSet<ScanObservation> Observations => Set<ScanObservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var scan = modelBuilder.Entity<ScanObservation>();
        scan.ToTable("scan_observations", "integrations");
        scan.HasKey(x => x.EventId).HasName("pk_scan_observations");
        scan.Property(x => x.EventId).HasColumnName("event_id").ValueGeneratedNever();
        scan.Property(x => x.SchemaVersion).HasColumnName("schema_version");
        scan.Property(x => x.EventType).HasColumnName("event_type").IsRequired();
        scan.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        scan.Property(x => x.TenantId).HasColumnName("tenant_id");
        scan.Property(x => x.PlantId).HasColumnName("plant_id");
        scan.Property(x => x.StationId).HasColumnName("station_id");
        scan.Property(x => x.DeviceId).HasColumnName("device_id");
        scan.Property(x => x.ObservedAtUtc).HasColumnName("observed_at_utc");
        scan.Property(x => x.GatewayAcceptedAtUtc).HasColumnName("gateway_accepted_at_utc");
        scan.Property(x => x.CloudReceivedAtUtc).HasColumnName("cloud_received_at_utc");
        scan.Property(x => x.IdentifierTechnology).HasColumnName("identifier_technology").IsRequired();
        scan.Property(x => x.IdentifierValue).HasColumnName("identifier_value").IsRequired();
        scan.Property(x => x.PayloadJson).HasColumnName("payload_json");
        scan.HasIndex(x => new { x.TenantId, x.PlantId, x.CloudReceivedAtUtc })
            .HasDatabaseName("ix_scan_observations_tenant_plant_received");
    }
}
