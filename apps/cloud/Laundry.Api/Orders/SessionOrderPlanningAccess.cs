using System.Data.Common;
using Laundry.Api.Customers;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Orders;

// Cloud-host composition: modules keep their own rules/entities and share only this
// connection/transaction for a coordinated relational write in the modular monolith.
public sealed class SessionOrderPlanningAccess(ManagementActor actor,
    DbContextOptions<ManagementAccessDbContext> membershipOptions,
    DbContextOptions<CustomerDbContext> customerOptions) : ITransactionalOrderPlanningAccess
{
    public string? ActorDisplayName => ManagementAuthentication.SafeAccountName(actor.DisplayName);
    private ManagementAccessDbContext? _memberships;
    private CustomerDbContext? _customers;
    private OrderPlanningScope? _boundScope;

    private bool Matches(OrderPlanningScope scope) => actor.IsAuthenticated && actor.Kind == ManagementActorKind.Human &&
        scope.ActorIssuer == actor.Issuer && scope.ActorSubject == actor.Subject &&
        (_boundScope is null || _boundScope == scope);

    public async Task<bool> IsAllowedAsync(OrderPlanningScope scope, string permission, CancellationToken ct)
    {
        if (!Matches(scope)) return false;
        if (_memberships is not null)
            return await new ManagementAccessStore(_memberships).IsAllowedForMutationAsync(actor, scope.TenantId, scope.PlantId, permission, ct);
        await using var db = new ManagementAccessDbContext(membershipOptions);
        return await new ManagementAccessStore(db).IsAllowedAsync(actor, scope.TenantId, scope.PlantId, permission, ct);
    }

    public async Task<bool> IsCustomerEligibleAsync(OrderPlanningScope scope, Guid customerId, CancellationToken ct)
    {
        if (!Matches(scope)) return false;
        if (_customers is not null)
            return await new CustomerStore(_customers).IsEligibleForMutationAsync(scope.TenantId, scope.PlantId, customerId, ct);
        await using var db = new CustomerDbContext(customerOptions);
        return await new CustomerStore(db).IsEligibleAsync(scope.TenantId, scope.PlantId, customerId, ct);
    }

    public async Task<IAsyncDisposable> BindTransactionAsync(DbConnection connection, DbTransaction transaction,
        OrderPlanningScope scope, CancellationToken ct)
    {
        if (!Matches(scope)) throw new OrderPlanningException("access_denied");
        if (_memberships is not null) throw new InvalidOperationException("Order access is already bound.");
        _memberships = new ManagementAccessDbContext(new DbContextOptionsBuilder<ManagementAccessDbContext>().UseNpgsql(connection).Options);
        _customers = new CustomerDbContext(new DbContextOptionsBuilder<CustomerDbContext>().UseNpgsql(connection).Options);
        _boundScope = scope;
        try
        {
            await _memberships.Database.UseTransactionAsync(transaction, ct);
            await _customers.Database.UseTransactionAsync(transaction, ct);
            return new Lease(this);
        }
        catch
        {
            await DisposeBoundContextsAsync();
            throw;
        }
    }

    public async Task<OrderPublicationCustomer?> ReadPublicationCustomerAsync(OrderPlanningScope scope, Guid customerId, CancellationToken ct)
    {
        if (!Matches(scope) || _customers is null || _boundScope != scope)
            throw new InvalidOperationException("publication_customer_requires_bound_transaction");
        var labels = await new CustomerStore(_customers).ReadEligibleLabelsForMutationAsync(scope.TenantId, scope.PlantId, customerId, ct);
        return labels is null ? null : new(scope.TenantId, labels.Id, labels.Code, labels.DisplayName);
    }

    private async ValueTask DisposeBoundContextsAsync()
    {
        if (_customers is not null) await _customers.DisposeAsync();
        if (_memberships is not null) await _memberships.DisposeAsync();
        _customers = null;
        _memberships = null;
        _boundScope = null;
    }

    private sealed class Lease(SessionOrderPlanningAccess owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => owner.DisposeBoundContextsAsync();
    }
}
