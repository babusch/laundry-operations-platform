using System.Collections.Immutable;
using Laundry.Api.Orders;
using Laundry.Api.Security;

namespace Laundry.Api.Tests;

public sealed class ManagementOrderAuthorizationTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _plant = Guid.NewGuid();
    private readonly ManagementActor _actor = new("https://identity.example.test/realm", "synthetic-person", ManagementActorKind.Human, true);

    [Theory]
    [InlineData(OrderPlanningPermissions.Read)]
    [InlineData(OrderPlanningPermissions.Create)]
    [InlineData(OrderPlanningPermissions.EditPlan)]
    public void ManagementBundle_GrantsOnlyAssignedScopeAndPreservesStableActor(string permission)
    {
        Assert.True(Authorize(_actor, [Grant()], permission, out var scope));
        Assert.Equal(new OrderPlanningScope(_tenant, _plant, _actor.Issuer, _actor.Subject), scope);
        Assert.False(ManagementOrderAuthorization.TryAuthorize(_actor, [Grant()], Guid.NewGuid(), _plant, permission, out _));
        Assert.False(ManagementOrderAuthorization.TryAuthorize(_actor, [Grant()], _tenant, Guid.NewGuid(), permission, out _));
    }

    [Theory]
    [InlineData(ManagementActorKind.Gateway)]
    [InlineData(ManagementActorKind.TrustedSource)]
    [InlineData(ManagementActorKind.Unknown)]
    public void NonHumanActor_CannotUseManagementPermissionsEvenWithMatchingGrant(ManagementActorKind kind)
    {
        Assert.False(Authorize(_actor with { Kind = kind }, [Grant()], OrderPlanningPermissions.Create, out var scope));
        Assert.Null(scope);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("blank_issuer")]
    [InlineData("blank_subject")]
    [InlineData("different_issuer")]
    [InlineData("different_subject")]
    public void MissingOrMismatchedIdentity_IsDenied(string invalid)
    {
        var actor = invalid switch
        {
            "anonymous" => _actor with { IsAuthenticated = false },
            "blank_issuer" => _actor with { Issuer = " " },
            "blank_subject" => _actor with { Subject = " " },
            "different_issuer" => _actor with { Issuer = "https://other.example.test/realm" },
            _ => _actor with { Subject = "another-person" }
        };
        Assert.False(Authorize(actor, [Grant()], OrderPlanningPermissions.Read, out var scope));
        Assert.Null(scope);
    }

    [Fact]
    public void ReadOnlyGrant_DoesNotImplyCreateOrEdit()
    {
        var grant = Grant() with { Permissions = ImmutableHashSet.Create(OrderPlanningPermissions.Read) };
        Assert.True(Authorize(_actor, [grant], OrderPlanningPermissions.Read, out _));
        Assert.False(Authorize(_actor, [grant], OrderPlanningPermissions.Create, out _));
        Assert.False(Authorize(_actor, [grant], OrderPlanningPermissions.EditPlan, out _));
    }

    [Theory]
    [InlineData("orders.complete")]
    [InlineData("sync.replay")]
    [InlineData("*")]
    [InlineData("Orders.Create")]
    [InlineData("")]
    public void UnknownOrUnapprovedPermission_IsDeniedEvenIfGranted(string permission)
    {
        var grant = Grant() with { Permissions = ImmutableHashSet.Create(permission) };
        Assert.False(Authorize(_actor, [grant], permission, out _));
    }

    [Fact]
    public void PermissionsAndMembership_CannotBeCombinedAcrossPlants()
    {
        var here = Grant() with { Permissions = ImmutableHashSet.Create(OrderPlanningPermissions.Read) };
        var elsewhere = Grant() with { PlantId = Guid.NewGuid() };
        Assert.False(Authorize(_actor, [here, elsewhere], OrderPlanningPermissions.Create, out _));
    }

    [Fact]
    public void MissingInactiveOrRevokedGrant_DeniesTheNextEvaluation()
    {
        Assert.False(Authorize(_actor, [], OrderPlanningPermissions.Read, out _));
        var grant = Grant();
        Assert.True(Authorize(_actor, [grant], OrderPlanningPermissions.Read, out _));
        Assert.False(Authorize(_actor, [grant with { IsActive = false }], OrderPlanningPermissions.Read, out _));
    }

    [Fact]
    public void EmptyScope_IsDenied()
    {
        Assert.False(ManagementOrderAuthorization.TryAuthorize(_actor, [Grant()], Guid.Empty, _plant, OrderPlanningPermissions.Read, out _));
        Assert.False(ManagementOrderAuthorization.TryAuthorize(_actor, [Grant()], _tenant, Guid.Empty, OrderPlanningPermissions.Read, out _));
    }

    [Fact]
    public void GrantComparer_CannotMakePermissionNamesCaseInsensitive()
    {
        var grant = Grant() with { Permissions = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "ORDERS.CREATE") };
        Assert.False(Authorize(_actor, [grant], OrderPlanningPermissions.Create, out _));
    }

    private ManagementPlantGrant Grant() => new(_actor.Issuer, _actor.Subject, _tenant, _plant, true, OrderPlanningPermissions.Management);

    private bool Authorize(ManagementActor actor, IEnumerable<ManagementPlantGrant> grants, string permission,
        out OrderPlanningScope? scope) => ManagementOrderAuthorization.TryAuthorize(actor, grants, _tenant, _plant, permission, out scope);
}
