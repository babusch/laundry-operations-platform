namespace Laundry.Edge.Security;

public sealed record SourceIdentity(
    Guid SourceId,
    Guid CredentialId,
    Guid TenantId,
    Guid PlantId,
    Guid StationId,
    TrustedSourceKind PrincipalType,
    long ConfigurationVersion,
    IReadOnlySet<string> Permissions);

public readonly record struct SourceScope(Guid TenantId, Guid PlantId);

public readonly record struct AuthenticatedSourceCredential(Guid CredentialId, SourceCredentialKind Kind);

public interface ISourceAuthenticator
{
    ValueTask<SourceIdentity?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken);
}

public interface ISourceIdentityResolver
{
    Task<SourceIdentity?> ResolveAsync(AuthenticatedSourceCredential credential, SourceScope gatewayScope,
        CancellationToken cancellationToken);
}
