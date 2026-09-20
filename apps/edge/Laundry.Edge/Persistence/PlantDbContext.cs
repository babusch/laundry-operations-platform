using Microsoft.EntityFrameworkCore;
using Laundry.Edge.Scans;
using Laundry.Edge.Security;
using Laundry.Edge.Synchronization;

namespace Laundry.Edge.Persistence;

public sealed class PlantDbContext(DbContextOptions<PlantDbContext> options) : DbContext(options)
{
    public DbSet<LocalObservation> Observations => Set<LocalObservation>();
    public DbSet<OutboxEntry> Outbox => Set<OutboxEntry>();
    public DbSet<ReplayAudit> ReplayAudits => Set<ReplayAudit>();
    public DbSet<TrustedSource> TrustedSources => Set<TrustedSource>();
    public DbSet<SourceCredential> SourceCredentials => Set<SourceCredential>();
    public DbSet<SourcePermission> SourcePermissions => Set<SourcePermission>();
    public DbSet<SourceEnrollmentCode> SourceEnrollmentCodes => Set<SourceEnrollmentCode>();
    public DbSet<SourceSecurityAudit> SourceSecurityAudits => Set<SourceSecurityAudit>();

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

        var audit = modelBuilder.Entity<ReplayAudit>();
        audit.ToTable("replay_audit", "plant");
        audit.HasKey(x => x.RequestId);
        audit.Property(x => x.RequestId).HasColumnName("request_id").ValueGeneratedNever();
        audit.Property(x => x.EventId).HasColumnName("event_id");
        audit.Property(x => x.TenantId).HasColumnName("tenant_id");
        audit.Property(x => x.PlantId).HasColumnName("plant_id");
        audit.Property(x => x.PreviousAttempts).HasColumnName("previous_attempts");
        audit.Property(x => x.PreviousError).HasColumnName("previous_error");
        audit.Property(x => x.ReasonCode).HasColumnName("reason_code");
        audit.Property(x => x.Actor).HasColumnName("actor");
        audit.Property(x => x.RequestedAtUtc).HasColumnName("requested_at_utc");
        audit.HasIndex(x => new { x.TenantId, x.PlantId, x.EventId });
        audit.HasOne<LocalObservation>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);

        var source = modelBuilder.Entity<TrustedSource>();
        source.ToTable("trusted_sources", "plant");
        source.HasKey(x => x.SourceId);
        source.Property(x => x.SourceId).HasColumnName("source_id").ValueGeneratedNever();
        source.Property(x => x.TenantId).HasColumnName("tenant_id");
        source.Property(x => x.PlantId).HasColumnName("plant_id");
        source.Property(x => x.StationId).HasColumnName("station_id");
        source.Property(x => x.Kind).HasColumnName("source_kind").HasConversion<string>().HasMaxLength(32);
        source.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        source.Property(x => x.ConfigurationVersion).HasColumnName("configuration_version");
        source.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        source.Property(x => x.StatusChangedAtUtc).HasColumnName("status_changed_at_utc");
        source.HasIndex(x => new { x.TenantId, x.PlantId, x.StationId, x.Status });

        var credential = modelBuilder.Entity<SourceCredential>();
        credential.ToTable("source_credentials", "plant");
        credential.HasKey(x => x.CredentialId);
        credential.Property(x => x.CredentialId).HasColumnName("credential_id").ValueGeneratedNever();
        credential.Property(x => x.SourceId).HasColumnName("source_id");
        credential.Property(x => x.Kind).HasColumnName("credential_kind").HasConversion<string>().HasMaxLength(32);
        credential.Property(x => x.VerifierDigest).HasColumnName("verifier_digest").HasMaxLength(64);
        credential.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        credential.Property(x => x.IssuedAtUtc).HasColumnName("issued_at_utc");
        credential.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
        credential.Property(x => x.RevokedAtUtc).HasColumnName("revoked_at_utc");
        credential.HasIndex(x => new { x.Kind, x.VerifierDigest }).IsUnique();
        credential.HasOne<TrustedSource>().WithMany().HasForeignKey(x => x.SourceId)
            .OnDelete(DeleteBehavior.Restrict);

        var permission = modelBuilder.Entity<SourcePermission>();
        permission.ToTable("source_permissions", "plant");
        permission.HasKey(x => new { x.SourceId, x.Permission });
        permission.Property(x => x.SourceId).HasColumnName("source_id");
        permission.Property(x => x.Permission).HasColumnName("permission").HasMaxLength(100);
        permission.HasOne<TrustedSource>().WithMany().HasForeignKey(x => x.SourceId)
            .OnDelete(DeleteBehavior.Restrict);

        var enrollment = modelBuilder.Entity<SourceEnrollmentCode>();
        enrollment.ToTable("source_enrollment_codes", "plant");
        enrollment.HasKey(x => x.EnrollmentCodeId);
        enrollment.Property(x => x.EnrollmentCodeId).HasColumnName("enrollment_code_id").ValueGeneratedNever();
        enrollment.Property(x => x.SourceId).HasColumnName("source_id");
        enrollment.Property(x => x.CodeDigest).HasColumnName("code_digest").HasMaxLength(64);
        enrollment.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        enrollment.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
        enrollment.Property(x => x.RedeemedAtUtc).HasColumnName("redeemed_at_utc");
        enrollment.HasIndex(x => x.CodeDigest).IsUnique();
        enrollment.HasOne<TrustedSource>().WithMany().HasForeignKey(x => x.SourceId)
            .OnDelete(DeleteBehavior.Restrict);

        var securityAudit = modelBuilder.Entity<SourceSecurityAudit>();
        securityAudit.ToTable("source_security_audit", "plant");
        securityAudit.HasKey(x => x.AuditId);
        securityAudit.Property(x => x.AuditId).HasColumnName("audit_id").ValueGeneratedNever();
        securityAudit.Property(x => x.SourceId).HasColumnName("source_id");
        securityAudit.Property(x => x.CredentialId).HasColumnName("credential_id");
        securityAudit.Property(x => x.TenantId).HasColumnName("tenant_id");
        securityAudit.Property(x => x.PlantId).HasColumnName("plant_id");
        securityAudit.Property(x => x.Action).HasColumnName("action").HasConversion<string>().HasMaxLength(48);
        securityAudit.Property(x => x.Outcome).HasColumnName("outcome").HasConversion<string>().HasMaxLength(16);
        securityAudit.Property(x => x.ActorType).HasColumnName("actor_type").HasMaxLength(48);
        securityAudit.Property(x => x.ActorId).HasColumnName("actor_id").HasMaxLength(200);
        securityAudit.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(64);
        securityAudit.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        securityAudit.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
        securityAudit.HasIndex(x => new { x.TenantId, x.PlantId, x.OccurredAtUtc });
        securityAudit.HasOne<TrustedSource>().WithMany().HasForeignKey(x => x.SourceId)
            .OnDelete(DeleteBehavior.Restrict);
        securityAudit.HasOne<SourceCredential>().WithMany().HasForeignKey(x => x.CredentialId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceSourceSecurityInvariants();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnforceSourceSecurityInvariants();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceSourceSecurityInvariants()
    {
        foreach (var entry in ChangeTracker.Entries<TrustedSource>().Where(x => x.State == EntityState.Modified))
        {
            if (entry.Property(x => x.TenantId).IsModified || entry.Property(x => x.PlantId).IsModified ||
                entry.Property(x => x.StationId).IsModified || entry.Property(x => x.Kind).IsModified)
                throw new InvalidOperationException("Trusted-source scope and kind are immutable; create a new source.");
        }

        foreach (var entry in ChangeTracker.Entries<SourceCredential>().Where(x => x.State == EntityState.Modified))
        {
            if (entry.Property(x => x.SourceId).IsModified || entry.Property(x => x.Kind).IsModified ||
                entry.Property(x => x.VerifierDigest).IsModified || entry.Property(x => x.IssuedAtUtc).IsModified ||
                entry.Property(x => x.ExpiresAtUtc).IsModified)
                throw new InvalidOperationException("Credential ownership and verifier data are immutable; rotate the credential.");
        }

        if (ChangeTracker.Entries<SourceSecurityAudit>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Source security audit is append-only.");
    }
}
