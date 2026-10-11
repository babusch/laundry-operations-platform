using Laundry.Api.Customers;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Laundry.Api.Tests;

[Trait("Category", "Database")]
public sealed class CustomerDatabaseTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23").Build();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _plant = Guid.NewGuid();
    private DbContextOptions<CustomerDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _options = new DbContextOptionsBuilder<CustomerDbContext>().UseNpgsql(_postgres.GetConnectionString(),
            provider => provider.MigrationsHistoryTable("__customer_migrations", "customers")).Options;
        await using var db = new CustomerDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task DevelopmentSetup_CommitsCustomerServiceAndAuditTogetherAndIsIdempotent()
    {
        await using var db = new CustomerDbContext(_options);
        var store = new CustomerStore(db);
        Assert.True(await store.InitializeDevelopmentAsync(_tenant, _plant));
        Assert.False(await store.InitializeDevelopmentAsync(_tenant, _plant));
        var customer = await db.Customers.SingleAsync();
        Assert.Equal(CustomerStore.DevelopmentCustomer, customer.Id);
        Assert.Equal("DEMO-001", customer.Code);
        Assert.Equal(_tenant, customer.TenantId);
        Assert.Equal(TimeSpan.Zero, customer.CreatedAtUtc.Offset);
        Assert.Equal(1, await db.PlantServices.CountAsync());
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.True(await store.IsEligibleAsync(_tenant, _plant, customer.Id));
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.MigrateAsync();
        Assert.Single(await store.ReadChoicesAsync(_tenant, _plant));
    }

    [Fact]
    public async Task ConcurrentSetup_ConvergesWithoutDuplicateServiceOrAudit()
    {
        async Task<bool> Setup()
        {
            await using var db = new CustomerDbContext(_options);
            return await new CustomerStore(db).InitializeDevelopmentAsync(_tenant, _plant);
        }
        var results = await Task.WhenAll(Setup(), Setup(), Setup());
        Assert.Single(results, created => created);
        await using var check = new CustomerDbContext(_options);
        Assert.Equal(1, await check.Customers.CountAsync());
        Assert.Equal(1, await check.PlantServices.CountAsync());
        Assert.Equal(1, await check.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task Eligibility_RequiresExactTenantPlantAndCustomer()
    {
        await using var db = new CustomerDbContext(_options);
        var store = new CustomerStore(db);
        await store.InitializeDevelopmentAsync(_tenant, _plant);
        Assert.False(await store.IsEligibleAsync(Guid.NewGuid(), _plant, CustomerStore.DevelopmentCustomer));
        Assert.False(await store.IsEligibleAsync(_tenant, Guid.NewGuid(), CustomerStore.DevelopmentCustomer));
        Assert.False(await store.IsEligibleAsync(_tenant, _plant, Guid.NewGuid()));
        Assert.False(await store.IsEligibleAsync(Guid.Empty, _plant, CustomerStore.DevelopmentCustomer));
        Assert.Empty(await store.ReadChoicesAsync(Guid.NewGuid(), _plant));
        Assert.Empty(await store.ReadChoicesAsync(_tenant, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("service")]
    public async Task InactiveCustomerOrService_IsNotEligibleAndRepeatSetupDoesNotReactivate(string inactive)
    {
        await using var db = new CustomerDbContext(_options);
        var store = new CustomerStore(db);
        await store.InitializeDevelopmentAsync(_tenant, _plant);
        // Simulate a future privileged administration path; this slice has no such HTTP write.
        var sql = inactive == "customer" ? "UPDATE customers.laundry_customers SET is_active = false" : "UPDATE customers.plant_services SET is_active = false";
        await db.Database.ExecuteSqlRawAsync(sql);
        Assert.False(await store.InitializeDevelopmentAsync(_tenant, _plant));
        Assert.False(await store.IsEligibleAsync(_tenant, _plant, CustomerStore.DevelopmentCustomer));
        Assert.Empty(await store.ReadChoicesAsync(_tenant, _plant));
    }

    [Fact]
    public async Task SameTenantCustomerWithoutPlantService_IsNotSelectable()
    {
        await using var db = new CustomerDbContext(_options);
        var id = Guid.NewGuid();
        db.Customers.Add(new LaundryCustomer
        {
            Id = id,
            TenantId = _tenant,
            Code = "SYNTHETIC-NO-SERVICE",
            DisplayName = "Synthetic unserved customer",
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var store = new CustomerStore(db);
        Assert.False(await store.IsEligibleAsync(_tenant, _plant, id));
        Assert.Empty(await store.ReadChoicesAsync(_tenant, _plant));
    }

    [Fact]
    public async Task DatabaseForeignKey_RejectsCrossTenantCustomerService()
    {
        await using var db = new CustomerDbContext(_options);
        await new CustomerStore(db).InitializeDevelopmentAsync(_tenant, _plant);
        db.PlantServices.Add(new CustomerPlantService
        {
            TenantId = Guid.NewGuid(),
            PlantId = _plant,
            CustomerId = CustomerStore.DevelopmentCustomer,
            IsActive = true
        });
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    [Fact]
    public async Task AuditFailure_RollsBackCustomerAndServiceAndRetryRecovers()
    {
        await using var db = new CustomerDbContext(_options);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE customers.setup_audit_entries ADD CONSTRAINT test_failure CHECK (action = 'never')");
        await Assert.ThrowsAsync<DbUpdateException>(() => new CustomerStore(db).InitializeDevelopmentAsync(_tenant, _plant));
        await using var check = new CustomerDbContext(_options);
        Assert.Equal(0, await check.Customers.CountAsync());
        Assert.Equal(0, await check.PlantServices.CountAsync());
        Assert.Equal(0, await check.AuditEntries.CountAsync());
        await check.Database.ExecuteSqlRawAsync("ALTER TABLE customers.setup_audit_entries DROP CONSTRAINT test_failure");
        Assert.True(await new CustomerStore(check).InitializeDevelopmentAsync(_tenant, _plant));
    }

    [Fact]
    public async Task SetupAudit_RejectsSqlMutation()
    {
        await using var db = new CustomerDbContext(_options);
        await new CustomerStore(db).InitializeDevelopmentAsync(_tenant, _plant);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM customers.setup_audit_entries"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE customers.setup_audit_entries"));
    }

    [Fact]
    public async Task RepeatSetup_DoesNotAddAnotherPlantOrReuseIdInAnotherTenant()
    {
        await using var db = new CustomerDbContext(_options);
        var store = new CustomerStore(db);
        await store.InitializeDevelopmentAsync(_tenant, _plant);
        var otherPlant = Guid.NewGuid();
        Assert.False(await store.InitializeDevelopmentAsync(_tenant, otherPlant));
        Assert.False(await store.IsEligibleAsync(_tenant, otherPlant, CustomerStore.DevelopmentCustomer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeDevelopmentAsync(Guid.NewGuid(), _plant));
    }

    [Fact]
    public async Task ReadPreview_IsBoundedAndIncludesOnlyExplicitActiveServices()
    {
        await using var db = new CustomerDbContext(_options);
        for (var index = 0; index < 110; index++)
        {
            var id = Guid.NewGuid();
            db.Customers.Add(new LaundryCustomer
            {
                Id = id,
                TenantId = _tenant,
                Code = $"SIM-{index:D3}",
                DisplayName = "Synthetic preview customer",
                IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            db.PlantServices.Add(new CustomerPlantService { TenantId = _tenant, PlantId = _plant, CustomerId = id, IsActive = true });
        }
        await db.SaveChangesAsync();
        var choices = await new CustomerStore(db).ReadChoicesAsync(_tenant, _plant);
        Assert.Equal(101, choices.Length); // Endpoint returns 100 plus a truthful truncation indicator.
        Assert.Equal("SIM-000", choices[0].Code);
        Assert.Equal("SIM-100", choices[^1].Code);
    }
}
