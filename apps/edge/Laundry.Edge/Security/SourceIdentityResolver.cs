using Laundry.Edge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Edge.Security;

public sealed class SourceIdentityResolver(PlantDbContext database, TimeProvider clock) : ISourceIdentityResolver
{
    public async Task<SourceIdentity?> ResolveAsync(AuthenticatedSourceCredential credential,
        SourceScope gatewayScope, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        var resolved = await (
            from storedCredential in database.SourceCredentials.AsNoTracking()
            join source in database.TrustedSources.AsNoTracking()
                on storedCredential.SourceId equals source.SourceId
            where storedCredential.CredentialId == credential.CredentialId &&
                  storedCredential.Kind == credential.Kind &&
                  storedCredential.Status == SourceCredentialStatus.Active &&
                  storedCredential.RevokedAtUtc == null &&
                  storedCredential.IssuedAtUtc <= now &&
                  (storedCredential.ExpiresAtUtc == null || storedCredential.ExpiresAtUtc > now) &&
                  source.Status == TrustedSourceStatus.Active &&
                  source.TenantId == gatewayScope.TenantId &&
                  source.PlantId == gatewayScope.PlantId
            select new
            {
                source.SourceId,
                storedCredential.CredentialId,
                source.TenantId,
                source.PlantId,
                source.StationId,
                source.Kind,
                source.ConfigurationVersion
            }).SingleOrDefaultAsync(cancellationToken);

        if (resolved is null) return null;

        var permissions = await database.SourcePermissions.AsNoTracking()
            .Where(permission => permission.SourceId == resolved.SourceId)
            .Select(permission => permission.Permission)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);

        return new SourceIdentity(resolved.SourceId, resolved.CredentialId, resolved.TenantId, resolved.PlantId,
            resolved.StationId, resolved.Kind, resolved.ConfigurationVersion, permissions);
    }
}
