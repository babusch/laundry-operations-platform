using Microsoft.EntityFrameworkCore;
using Laundry.Edge.Scans;

namespace Laundry.Edge.Persistence;

public sealed class PlantDbContext(DbContextOptions<PlantDbContext> options) : DbContext(options)
{
    public DbSet<LocalObservation> Observations => Set<LocalObservation>();
    public DbSet<OutboxEntry> Outbox => Set<OutboxEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var scan = modelBuilder.Entity<LocalObservation>();
        scan.ToTable("observations", "plant");
        scan.HasKey(x => x.EventId);
        scan.Property(x => x.EventId).HasColumnName("event_id").ValueGeneratedNever();
        scan.Property(x => x.TenantId).HasColumnName("tenant_id");
        scan.Property(x => x.PlantId).HasColumnName("plant_id");
        scan.Property(x => x.AcceptedAtUtc).HasColumnName("accepted_at_utc");
        scan.Property(x => x.SubmissionJson).HasColumnName("submission_json").IsRequired();
        scan.Property(x => x.EventJson).HasColumnName("event_json").IsRequired();
        scan.HasIndex(x => new { x.TenantId, x.PlantId, x.AcceptedAtUtc });

        var outbox = modelBuilder.Entity<OutboxEntry>();
        outbox.ToTable("outbox", "plant");
        outbox.HasKey(x => x.EventId);
        outbox.Property(x => x.EventId).HasColumnName("event_id").ValueGeneratedNever();
        outbox.Property(x => x.Status).HasColumnName("status").IsRequired();
        outbox.HasIndex(x => x.Status);
        outbox.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        outbox.Property(x => x.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc");
        outbox.Property(x => x.LeaseId).HasColumnName("lease_id");
        outbox.Property(x => x.LeaseUntilUtc).HasColumnName("lease_until_utc");
        outbox.Property(x => x.CloudReceivedAtUtc).HasColumnName("cloud_received_at_utc");
        outbox.Property(x => x.LastError).HasColumnName("last_error");
        outbox.HasOne<LocalObservation>().WithOne().HasForeignKey<OutboxEntry>(x => x.EventId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
