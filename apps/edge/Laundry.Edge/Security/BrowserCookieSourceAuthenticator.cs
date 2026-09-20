using System.Security.Cryptography;
using Laundry.Edge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Edge.Security;

public sealed class BrowserCookieSourceAuthenticator(PlantDbContext database, ISourceIdentityResolver resolver,
    IConfiguration configuration) : ISourceAuthenticator
{
    public async ValueTask<SourceIdentity?> AuthenticateAsync(HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.Cookies.TryGetValue(SourceEnrollmentEndpoints.SourceCookieName, out var cookie))
            return null;

        var separator = cookie.IndexOf('.');
        if (separator <= 0 || separator == cookie.Length - 1 ||
            !Guid.TryParseExact(cookie.AsSpan(0, separator), "N", out var credentialId) ||
            !SourceSecretTokens.TryDigest(cookie[(separator + 1)..], out var presentedDigest))
            return null;

        var credential = await database.SourceCredentials.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CredentialId == credentialId && x.Kind == SourceCredentialKind.BrowserSecret, cancellationToken);
        if (credential is null || credential.VerifierDigest.Length != presentedDigest.Length ||
            !CryptographicOperations.FixedTimeEquals(credential.VerifierDigest, presentedDigest))
            return null;

        var tenantId = configuration.GetValue<Guid>("ScanAcceptance:TenantId");
        var plantId = configuration.GetValue<Guid>("ScanAcceptance:PlantId");
        if (tenantId == Guid.Empty || plantId == Guid.Empty) return null;

        return await resolver.ResolveAsync(
            new AuthenticatedSourceCredential(credential.CredentialId, credential.Kind),
            new SourceScope(tenantId, plantId), cancellationToken);
    }
}
