using System.Security.Cryptography;
using Laundry.Edge.Persistence;
using Laundry.Edge.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Edge.Tests;

public sealed class SourceIdentityResolverTests(AcceptanceFixture fixture) : IClassFixture<AcceptanceFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActiveCredentialResolvesOnlyInsideItsGatewayScope()
    {
        var source = NewSource();
        var credential = NewCredential(source.SourceId);
        await SeedAsync(source, [credential], [SourcePermissions.SubmitScans]);

        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var resolver = new SourceIdentityResolver(scope.ServiceProvider.GetRequiredService<PlantDbContext>(),
            new FixedTimeProvider(Now));

        var identity = await resolver.ResolveAsync(
            new AuthenticatedSourceCredential(credential.CredentialId, credential.Kind),
            new SourceScope(source.TenantId, source.PlantId), CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal(source.SourceId, identity.SourceId);
        Assert.Equal(source.StationId, identity.StationId);
        Assert.Equal(TrustedSourceKind.BrowserStation, identity.PrincipalType);
        Assert.Contains(SourcePermissions.SubmitScans, identity.Permissions);
        Assert.Null(typeof(SourceIdentity).GetProperty("DeviceId"));

        var wrongPlant = await resolver.ResolveAsync(
            new AuthenticatedSourceCredential(credential.CredentialId, credential.Kind),
            new SourceScope(source.TenantId, Guid.NewGuid()), CancellationToken.None);
        Assert.Null(wrongPlant);

        var wrongTenant = await resolver.ResolveAsync(
            new AuthenticatedSourceCredential(credential.CredentialId, credential.Kind),
            new SourceScope(Guid.NewGuid(), source.PlantId), CancellationToken.None);
        Assert.Null(wrongTenant);
    }

    [Fact]
    public async Task CredentialRotationAllowsBoundedOverlap()
    {
        var source = NewSource();
        var previous = NewCredential(source.SourceId);
        var replacement = NewCredential(source.SourceId);
        await SeedAsync(source, [previous, replacement], [SourcePermissions.SubmitScans]);

        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var resolver = new SourceIdentityResolver(scope.ServiceProvider.GetRequiredService<PlantDbContext>(),
            new FixedTimeProvider(Now));
        var gateway = new SourceScope(source.TenantId, source.PlantId);

        var previousIdentity = await resolver.ResolveAsync(
            new AuthenticatedSourceCredential(previous.CredentialId, previous.Kind), gateway,
            CancellationToken.None);
        var replacementIdentity = await resolver.ResolveAsync(
            new AuthenticatedSourceCredential(replacement.CredentialId, replacement.Kind), gateway,
            CancellationToken.None);

        Assert.Equal(source.SourceId, previousIdentity?.SourceId);
        Assert.Equal(source.SourceId, replacementIdentity?.SourceId);
        Assert.NotEqual(previousIdentity?.CredentialId, replacementIdentity?.CredentialId);
    }

    [Fact]
    public async Task InvalidCredentialLifecycleStatesDoNotResolve()
    {
        var source = NewSource();
        var superseded = NewCredential(source.SourceId) with { Status = SourceCredentialStatus.Superseded };
        var revoked = NewCredential(source.SourceId) with
        {
            Status = SourceCredentialStatus.Revoked,
            RevokedAtUtc = Now.AddMinutes(-1)
        };
        var expired = NewCredential(source.SourceId) with { ExpiresAtUtc = Now };
        var notYetIssued = NewCredential(source.SourceId) with { IssuedAtUtc = Now.AddMinutes(1) };
        await SeedAsync(source, [superseded, revoked, expired, notYetIssued], [SourcePermissions.SubmitScans]);

        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var resolver = new SourceIdentityResolver(scope.ServiceProvider.GetRequiredService<PlantDbContext>(),
            new FixedTimeProvider(Now));
        var gateway = new SourceScope(source.TenantId, source.PlantId);

        foreach (var credential in new[] { superseded, revoked, expired, notYetIssued })
        {
            var identity = await resolver.ResolveAsync(
                new AuthenticatedSourceCredential(credential.CredentialId, credential.Kind), gateway,
                CancellationToken.None);
            Assert.Null(identity);
        }
    }

    [Theory]
    [InlineData(TrustedSourceStatus.Disabled)]
    [InlineData(TrustedSourceStatus.Revoked)]
    public async Task LocalDisableOrRevocationStopsResolutionAndScopeCannotBeReassigned(
        TrustedSourceStatus blockedStatus)
    {
        var source = NewSource();
        var credential = NewCredential(source.SourceId);
        await SeedAsync(source, [credential], [SourcePermissions.SubmitScans]);

        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        var stored = await database.TrustedSources.SingleAsync(x => x.SourceId == source.SourceId,
            CancellationToken.None);
        stored.Status = blockedStatus;
        stored.StatusChangedAtUtc = Now;
        stored.ConfigurationVersion++;
        await database.SaveChangesAsync(CancellationToken.None);

        var resolver = new SourceIdentityResolver(database, new FixedTimeProvider(Now));
        var identity = await resolver.ResolveAsync(
            new AuthenticatedSourceCredential(credential.CredentialId, credential.Kind),
            new SourceScope(source.TenantId, source.PlantId), CancellationToken.None);
        Assert.Null(identity);

        database.Entry(stored).Property(x => x.StationId).CurrentValue = Guid.NewGuid();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.SaveChangesAsync(CancellationToken.None));
        Assert.Contains("scope", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SourceSecurityAuditIsAppendOnly()
    {
        var source = NewSource();
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        database.TrustedSources.Add(source);
        var audit = new SourceSecurityAudit
        {
            AuditId = Guid.NewGuid(),
            SourceId = source.SourceId,
            TenantId = source.TenantId,
            PlantId = source.PlantId,
            Action = SourceSecurityAction.SourceCreated,
            Outcome = SourceSecurityOutcome.Succeeded,
            ActorType = "development-system",
            ActorId = "source-registry-test",
            CorrelationId = Guid.NewGuid(),
            OccurredAtUtc = Now
        };
        database.SourceSecurityAudits.Add(audit);
        await database.SaveChangesAsync(CancellationToken.None);

        database.Entry(audit).Property(x => x.ReasonCode).CurrentValue = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.SaveChangesAsync(CancellationToken.None));

        database.ChangeTracker.Clear();
        var stored = await database.SourceSecurityAudits.SingleAsync(x => x.AuditId == audit.AuditId,
            CancellationToken.None);
        database.SourceSecurityAudits.Remove(stored);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.SaveChangesAsync(CancellationToken.None));
    }

    private async Task SeedAsync(TrustedSource source, IReadOnlyCollection<SourceCredential> credentials,
        IReadOnlyCollection<string> permissions)
    {
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PlantDbContext>();
        database.TrustedSources.Add(source);
        database.SourceCredentials.AddRange(credentials);
        database.SourcePermissions.AddRange(permissions.Select(permission => new SourcePermission
        {
            SourceId = source.SourceId,
            Permission = permission
        }));
        await database.SaveChangesAsync(CancellationToken.None);
    }

    private static TrustedSource NewSource() => new()
    {
        SourceId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        PlantId = Guid.NewGuid(),
        StationId = Guid.NewGuid(),
        Kind = TrustedSourceKind.BrowserStation,
        Status = TrustedSourceStatus.Active,
        ConfigurationVersion = 1,
        CreatedAtUtc = Now.AddHours(-1),
        StatusChangedAtUtc = Now.AddHours(-1)
    };

    private static SourceCredential NewCredential(Guid sourceId)
    {
        var id = Guid.NewGuid();
        return new SourceCredential
        {
            CredentialId = id,
            SourceId = sourceId,
            Kind = SourceCredentialKind.BrowserSecret,
            VerifierDigest = SHA256.HashData(id.ToByteArray()),
            Status = SourceCredentialStatus.Active,
            IssuedAtUtc = Now.AddMinutes(-5),
            ExpiresAtUtc = Now.AddDays(30)
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
