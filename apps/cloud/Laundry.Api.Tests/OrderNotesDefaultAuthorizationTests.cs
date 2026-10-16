using Laundry.Api.Orders;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Tests;

public sealed partial class OrderCreateEndpointTests
{
    [Fact]
    public async Task NotesDefault_RealMembershipRequiresExplicitGrantAndRevocationBlocksRetry()
    {
        var scope = new OrderPlanningScope(Tenant, Plant, ManagementAccessStore.DevelopmentIssuer, _subject);
        var actor = new ManagementActor(scope.ActorIssuer, scope.ActorSubject, ManagementActorKind.Human, true);
        var store = new OrderPlanningStore(_orders, new SessionOrderPlanningAccess(actor, _members, _customers), TimeProvider.System);
        var key = Guid.NewGuid();
        Assert.Equal(new(false, 0), await store.ReadNotesDefaultAsync(scope));
        Assert.Equal("access_denied", (await Assert.ThrowsAsync<OrderPlanningException>(() => store.ChangeNotesDefaultAsync(scope, key, 0, true))).Code);
        await using var db = new ManagementAccessDbContext(_members);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO identity_access.plant_membership_permissions (membership_id,permission) SELECT id,{OrderPlanningPermissions.EditNotesDefault} FROM identity_access.plant_memberships WHERE tenant_id = {Tenant} AND plant_id = {Plant} AND actor_subject = {_subject}");
        Assert.Equal(new(true, 1), await store.ChangeNotesDefaultAsync(scope, key, 0, true));
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity_access.plant_membership_permissions WHERE permission = {OrderPlanningPermissions.EditNotesDefault}");
        Assert.Equal("access_denied", (await Assert.ThrowsAsync<OrderPlanningException>(() => store.ChangeNotesDefaultAsync(scope, key, 0, true))).Code);
        await using var orders = new OrderDbContext(_orders);
        Assert.Single(await orders.NotesDefaultCommands.ToArrayAsync());
        Assert.Empty(await orders.Orders.ToArrayAsync());
    }
}
