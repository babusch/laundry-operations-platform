using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Customers;

// Stable module-owned application contract, not shared EF entities or hardware concepts.
public interface ICustomerPlantEligibility
{
    Task<bool> IsEligibleAsync(Guid tenantId, Guid plantId, Guid customerId, CancellationToken ct = default);
}

public sealed record CustomerChoice(Guid Id, string Code, string DisplayName);

public sealed class CustomerStore(CustomerDbContext database) : ICustomerPlantEligibility
{
    public static readonly Guid DevelopmentCustomer = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private IQueryable<LaundryCustomer> Eligible(Guid tenantId, Guid plantId) =>
        from customer in database.Customers.AsNoTracking()
        join service in database.PlantServices.AsNoTracking()
            on new { customer.TenantId, CustomerId = customer.Id } equals new { service.TenantId, service.CustomerId }
        where customer.TenantId == tenantId && service.PlantId == plantId && customer.IsActive && service.IsActive
        select customer;

    public Task<bool> IsEligibleAsync(Guid tenantId, Guid plantId, Guid customerId, CancellationToken ct = default) =>
        Eligible(tenantId, plantId).AnyAsync(x => x.Id == customerId, ct);

    // Hold customer and its exact service link through the owning business transaction.
    public async Task<bool> IsEligibleForMutationAsync(Guid tenantId, Guid plantId, Guid customerId, CancellationToken ct = default)
    {
        if (database.Database.CurrentTransaction is null) throw new InvalidOperationException("Mutation eligibility requires the owning transaction.");
        var customer = await database.Customers.FromSqlInterpolated($"SELECT * FROM customers.laundry_customers WHERE tenant_id = {tenantId} AND id = {customerId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (customer is null || !customer.IsActive) return false;
        var service = await database.PlantServices.FromSqlInterpolated($"SELECT * FROM customers.plant_services WHERE tenant_id = {tenantId} AND plant_id = {plantId} AND customer_id = {customerId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        return service is { IsActive: true };
    }

    // Bounded Development preview, not a production paginated customer-management API.
    public Task<CustomerChoice[]> ReadChoicesAsync(Guid tenantId, Guid plantId, CancellationToken ct = default) =>
        Eligible(tenantId, plantId).OrderBy(x => x.Code).ThenBy(x => x.Id).Take(101)
            .Select(x => new CustomerChoice(x.Id, x.Code, x.DisplayName)).ToArrayAsync(ct);

    // Called only by the explicit host-guarded Development setup command, never on login.
    public async Task<bool> InitializeDevelopmentAsync(Guid tenantId, Guid plantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || plantId == Guid.Empty) throw new InvalidOperationException("Development customer scope is incomplete.");
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(72020261011)", ct);
        var existing = await database.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == DevelopmentCustomer, ct);
        if (existing is not null)
        {
            if (existing.TenantId != tenantId) throw new InvalidOperationException("The demonstration customer ID already belongs to another tenant.");
            // Re-running setup must not silently undo a later edit/deactivation or add plant access.
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        database.Customers.Add(new LaundryCustomer
        {
            Id = DevelopmentCustomer,
            TenantId = tenantId,
            Code = "DEMO-001",
            DisplayName = "Demo laundry customer",
            IsActive = true,
            CreatedAtUtc = now
        });
        database.PlantServices.Add(new CustomerPlantService
        {
            TenantId = tenantId,
            PlantId = plantId,
            CustomerId = DevelopmentCustomer,
            IsActive = true
        });
        database.AuditEntries.Add(new CustomerSetupAudit
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = DevelopmentCustomer,
            Action = "development_customer_and_service_created",
            RecordedAtUtc = now,
            RecordedBy = "local-development-setup:" + Environment.UserName,
            AfterState = JsonSerializer.Serialize(new
            {
                tenantId,
                plantId,
                customerId = DevelopmentCustomer,
                code = "DEMO-001",
                displayName = "Demo laundry customer",
                customerActive = true,
                serviceActive = true
            })
        });
        await database.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
