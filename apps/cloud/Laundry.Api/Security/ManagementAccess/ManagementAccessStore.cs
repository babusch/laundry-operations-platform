using System.Collections.Immutable;
using System.Text.Json;
using Laundry.Api.Orders;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Security.ManagementAccess;

public sealed record ManagementAssignment(Guid TenantId, Guid PlantId, string[] Permissions);

public sealed class ManagementAccessStore(ManagementAccessDbContext database)
{
    // Existing synthetic company/plant attribution used by the Development gateway.
    public static readonly Guid DevelopmentTenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly Guid DevelopmentPlant = Guid.Parse("22222222-2222-4222-8222-222222222222");
    public const string DevelopmentIssuer = "https://localhost:8443/realms/laundry-development";

    public async Task<ManagementAssignment[]> ReadAssignmentsAsync(ManagementActor actor, CancellationToken ct = default)
    {
        if (!ValidHuman(actor)) return [];
        var rows = await database.Memberships.AsNoTracking().Include(x => x.Permissions)
            .Where(x => x.ActorIssuer == actor.Issuer && x.ActorSubject == actor.Subject && x.IsActive)
            .OrderBy(x => x.TenantId).ThenBy(x => x.PlantId).ToListAsync(ct);
        return rows.Select(x => new ManagementAssignment(x.TenantId, x.PlantId,
            x.Permissions.Select(p => p.Permission).Where(OrderPlanningPermissions.IsKnown).Order(StringComparer.Ordinal).ToArray())).ToArray();
    }

    public async Task<bool> IsAllowedAsync(ManagementActor actor, Guid tenantId, Guid plantId,
        string permission, CancellationToken ct = default)
    {
        if (!ValidHuman(actor) || !OrderPlanningPermissions.IsKnown(permission)) return false;
        var member = await database.Memberships.AsNoTracking().Include(x => x.Permissions).SingleOrDefaultAsync(x =>
            x.ActorIssuer == actor.Issuer && x.ActorSubject == actor.Subject &&
            x.TenantId == tenantId && x.PlantId == plantId && x.IsActive, ct);
        if (member is null) return false;
        var grant = new ManagementPlantGrant(member.ActorIssuer, member.ActorSubject, member.TenantId,
            member.PlantId, member.IsActive, member.Permissions.Select(x => x.Permission).ToImmutableHashSet(StringComparer.Ordinal));
        return ManagementOrderAuthorization.TryAuthorize(actor, [grant], tenantId, plantId, permission, out _);
    }

    // Hold membership and permission rows until the owning business transaction completes.
    public async Task<bool> IsAllowedForMutationAsync(ManagementActor actor, Guid tenantId, Guid plantId,
        string permission, CancellationToken ct = default)
    {
        if (database.Database.CurrentTransaction is null) throw new InvalidOperationException("Mutation authorization requires the owning transaction.");
        if (!ValidHuman(actor) || !OrderPlanningPermissions.IsKnown(permission)) return false;
        var membership = await database.Memberships.FromSqlInterpolated($"SELECT * FROM identity_access.plant_memberships WHERE actor_issuer = {actor.Issuer} AND actor_subject = {actor.Subject} AND tenant_id = {tenantId} AND plant_id = {plantId} AND is_active = TRUE FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (membership is null) return false;
        return await database.Permissions.FromSqlInterpolated($"SELECT * FROM identity_access.plant_membership_permissions WHERE membership_id = {membership.Id} AND permission = {permission} FOR SHARE")
            .AsNoTracking().AnyAsync(ct);
    }

    // Explicit local Development provisioning only. Never call automatically on login.
    // The API host guards this command against non-Development use; it has no HTTP route.
    public async Task<bool> InitializeDevelopmentAsync(string subject, CancellationToken ct = default)
    {
        if (!Guid.TryParse(subject, out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Supply the demo user's immutable Keycloak ID for Development setup.");
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(71920261011)", ct);
        var existing = await database.Memberships.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ActorIssuer == DevelopmentIssuer && x.ActorSubject == subject &&
            x.TenantId == DevelopmentTenant && x.PlantId == DevelopmentPlant, ct);
        if (existing is not null)
        {
            // Do not reactivate revoked membership or overwrite an administrator's later edits.
            return false;
        }

        var membership = new PlantMembership
        {
            Id = Guid.NewGuid(),
            ActorIssuer = DevelopmentIssuer,
            ActorSubject = subject,
            TenantId = DevelopmentTenant,
            PlantId = DevelopmentPlant,
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        membership.Permissions = OrderPlanningPermissions.Management.Order(StringComparer.Ordinal)
            .Select(p => new PlantMembershipPermission { MembershipId = membership.Id, Permission = p }).ToList();
        database.Memberships.Add(membership);
        database.AuditEntries.Add(new MembershipAuditEntry
        {
            Id = Guid.NewGuid(),
            MembershipId = membership.Id,
            MembershipVersion = 1,
            Action = "development_membership_created",
            RecordedAtUtc = membership.CreatedAtUtc,
            RecordedBy = "local-development-setup:" + Environment.UserName,
            AfterState = JsonSerializer.Serialize(new
            {
                membership.ActorIssuer,
                membership.ActorSubject,
                membership.TenantId,
                membership.PlantId,
                membership.IsActive,
                permissions = membership.Permissions.Select(x => x.Permission)
            })
        });
        await database.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static bool ValidHuman(ManagementActor actor) => actor.IsAuthenticated && actor.Kind == ManagementActorKind.Human &&
        !string.IsNullOrWhiteSpace(actor.Issuer) && !string.IsNullOrWhiteSpace(actor.Subject);
}
