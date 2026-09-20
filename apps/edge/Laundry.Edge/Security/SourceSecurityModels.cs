namespace Laundry.Edge.Security;

public enum TrustedSourceKind
{
    BrowserStation,
    RemoteAdapter,
    LocalAdapter,
    InternalAdapter
}

public enum TrustedSourceStatus
{
    Pending,
    Active,
    Disabled,
    Revoked
}

public enum SourceCredentialKind
{
    BrowserSecret,
    ClientCertificate,
    LocalProcess,
    Internal
}

public enum SourceCredentialStatus
{
    Active,
    Superseded,
    Revoked
}

public enum SourceSecurityAction
{
    SourceCreated,
    SourceDisabled,
    SourceEnabled,
    SourceRevoked,
    EnrollmentCodeCreated,
    EnrollmentCompleted,
    CredentialRotated,
    CredentialRevoked,
    AdministrativeAttemptRejected
}

public enum SourceSecurityOutcome
{
    Succeeded,
    Rejected
}

public static class SourcePermissions
{
    public const string SubmitScans = "scans.submit";
}

public sealed record TrustedSource
{
    public required Guid SourceId { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid PlantId { get; init; }
    public required Guid StationId { get; init; }
    public required TrustedSourceKind Kind { get; init; }
    public required TrustedSourceStatus Status { get; set; }
    public required long ConfigurationVersion { get; set; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset StatusChangedAtUtc { get; set; }
}

public sealed record SourceCredential
{
    public required Guid CredentialId { get; init; }
    public required Guid SourceId { get; init; }
    public required SourceCredentialKind Kind { get; init; }
    public required byte[] VerifierDigest { get; init; }
    public required SourceCredentialStatus Status { get; set; }
    public required DateTimeOffset IssuedAtUtc { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed record SourcePermission
{
    public required Guid SourceId { get; init; }
    public required string Permission { get; init; }
}

public sealed record SourceEnrollmentCode
{
    public required Guid EnrollmentCodeId { get; init; }
    public required Guid SourceId { get; init; }
    public required byte[] CodeDigest { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset? RedeemedAtUtc { get; set; }
}

public sealed record SourceSecurityAudit
{
    public required Guid AuditId { get; init; }
    public Guid? SourceId { get; init; }
    public Guid? CredentialId { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid PlantId { get; init; }
    public required SourceSecurityAction Action { get; init; }
    public required SourceSecurityOutcome Outcome { get; init; }
    public required string ActorType { get; init; }
    public required string ActorId { get; init; }
    public string? ReasonCode { get; init; }
    public required Guid CorrelationId { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
}
