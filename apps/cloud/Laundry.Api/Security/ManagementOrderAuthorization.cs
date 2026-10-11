using System.Collections.Immutable;
using Laundry.Api.Orders;

namespace Laundry.Api.Security;

public enum ManagementActorKind
{
    Unknown,
    Human,
    Gateway,
    TrustedSource
}

// Trusted inputs to a pure authorization rule, NOT a login/session or proof of authentication.
// The future authentication adapter must validate issuer, audience, expiry, signature/session,
// and human client provenance before constructing this actor. Never bind it from request JSON.
public sealed record ManagementActor(string Issuer, string Subject, ManagementActorKind Kind, bool IsAuthenticated);

// Application-owned access data, not caller-asserted scope or unchecked token role names.
// Keep permission and plant together so access to one plant cannot leak into another.
public sealed record ManagementPlantGrant(string ActorIssuer, string ActorSubject,
    Guid TenantId, Guid PlantId, bool IsActive, ImmutableHashSet<string> Permissions);

public static class ManagementOrderAuthorization
{
    public static bool TryAuthorize(ManagementActor actor, IEnumerable<ManagementPlantGrant> grants,
        Guid tenantId, Guid plantId, string permission, out OrderPlanningScope? scope)
    {
        scope = null;
        if (!actor.IsAuthenticated || actor.Kind != ManagementActorKind.Human ||
            string.IsNullOrWhiteSpace(actor.Issuer) || string.IsNullOrWhiteSpace(actor.Subject) ||
            tenantId == Guid.Empty || plantId == Guid.Empty || !OrderPlanningPermissions.IsKnown(permission))
        {
            return false;
        }

        var permitted = grants.Any(grant => grant.IsActive &&
            string.Equals(grant.ActorIssuer, actor.Issuer, StringComparison.Ordinal) &&
            string.Equals(grant.ActorSubject, actor.Subject, StringComparison.Ordinal) &&
            grant.TenantId == tenantId && grant.PlantId == plantId &&
            grant.Permissions.Any(value => string.Equals(value, permission, StringComparison.Ordinal)));
        if (!permitted)
        {
            return false;
        }

        scope = new OrderPlanningScope(tenantId, plantId, actor.Issuer, actor.Subject);
        return true;
    }
}
