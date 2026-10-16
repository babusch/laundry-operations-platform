using System.Text.Json;
using Laundry.Api.Integrations.Scans;
using Laundry.Api.Orders;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Laundry.Api.Tests;

[Trait("Category", "Database")]
public sealed partial class OrderPlanningDatabaseTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23").Build();
    private readonly OrderPlanningScope _scope = new(Guid.NewGuid(), Guid.NewGuid(), "https://identity.example.test", "synthetic-manager");
    private readonly OrderPlan _plan = new(Guid.NewGuid(), new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 3));
    private DbContextOptions<OrderDbContext> _options = null!;
    private TestAccess _access = null!;
    private OrderPlanningStore _store = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _options = Options(_postgres.GetConnectionString());
        _access = new TestAccess(_scope, _plan.CustomerId);
        _store = new OrderPlanningStore(_options, _access, TimeProvider.System);
        await using var db = new OrderDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task PlannedCustomerAndAllOtherFields_CanChangeWithAudit()
    {
        var created = await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        var nextCustomer = Guid.NewGuid();
        _access.AdditionalCustomer = nextCustomer;
        var nextPlan = new OrderPlan(nextCustomer, new DateOnly(2030, 2, 1), new DateOnly(2030, 2, 4), "DEMO-02", "New plan");
        await _store.UpdateAsync(_scope, Guid.NewGuid(), Id(created), 1, nextPlan);
        Assert.Equal(nextPlan, (await _store.GetAsync(_scope, Id(created)))!.Plan());
        await using var db = new OrderDbContext(_options);
        var audit = await db.AuditEntries.SingleAsync(x => x.OrderVersion == 2);
        Assert.Contains(_plan.CustomerId.ToString(), audit.BeforePlan!);
        Assert.Contains(nextCustomer.ToString(), audit.AfterPlan);
    }

    [Fact]
    public async Task NormalizedRetryAndChangedOperation_AreHandledExplicitly()
    {
        var key = Guid.NewGuid();
        var created = await _store.CreateAsync(_scope, key, _plan with { CustomerReference = "  " });
        Assert.Equal(created, await _store.CreateAsync(_scope, key, _plan));
        await Error("idempotency_conflict", () => _store.UpdateAsync(_scope, key, Id(created), 1, _plan));
        await Error("invalid_command", () => _store.CreateAsync(_scope, Guid.Empty, _plan));
    }

    [Fact]
    public async Task EfConcurrencyToken_RejectsUncoordinatedStaleWrites()
    {
        await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        await using var first = new OrderDbContext(_options);
        await using var second = new OrderDbContext(_options);
        var one = await first.Orders.SingleAsync();
        var two = await second.Orders.SingleAsync();
        one.Notes = "First";
        one.Version++;
        await first.SaveChangesAsync();
        two.Notes = "Stale";
        two.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        // Simulate a future lifecycle writer without defining/implementing its state machine.
        await first.Database.ExecuteSqlRawAsync("ALTER TABLE orders.service_orders DROP CONSTRAINT ck_order_status");
        await first.Database.ExecuteSqlRawAsync("UPDATE orders.service_orders SET status = 'non-planned-test-state'");
        await Error("order_not_planned", () => _store.UpdateAsync(_scope, Guid.NewGuid(), one.Id, 2, _plan));
    }

    [Fact]
    public async Task Create_RoundTripsDatesActorAuditAndReceiptTogether()
    {
        var key = Guid.NewGuid();
        var result = await _store.CreateAsync(_scope, key, _plan with { Notes = "Synthetic notes" });
        var order = await _store.GetAsync(_scope, Id(result));
        Assert.NotNull(order);
        Assert.Equal("ORD-000001", order.OrderNumber);
        Assert.Equal(_plan.PlannedReceivingDate, order.PlannedReceivingDate);
        Assert.Equal(TimeSpan.Zero, order.CreatedAtUtc.Offset);
        Assert.Equal(_scope.ActorSubject, order.CreatedBySubject);
        Assert.Equal("planned", order.Status);
        await using var db = new OrderDbContext(_options);
        var audit = await db.AuditEntries.SingleAsync();
        Assert.Null(audit.BeforePlan);
        Assert.Equal(key, audit.RequestId);
        Assert.Equal("order_created", audit.Action);
        Assert.Equal(1, await db.CommandReceipts.CountAsync());
        Assert.DoesNotContain("actorSubject", result.Body);
    }

    [Fact]
    public async Task ConcurrentIdenticalRetries_ConvergeAndChangedKeyUseConflicts()
    {
        var key = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _store.CreateAsync(_scope, key, _plan)));
        Assert.All(results, result => Assert.Equal(results[0], result));
        await Error("idempotency_conflict", () => _store.CreateAsync(_scope, key, _plan with { Notes = "changed" }));
        await using var db = new OrderDbContext(_options);
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.Equal(1, await db.CommandReceipts.CountAsync());
    }

    [Fact]
    public async Task SeparateOrdersSameDates_GetDistinctNumbers()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _store.CreateAsync(_scope, Guid.NewGuid(), _plan)));
        Assert.Equal(4, results.Select(Id).Distinct().Count());
        await using var db = new OrderDbContext(_options);
        Assert.Equal(4, await db.Orders.Select(x => x.OrderNumber).Distinct().CountAsync());
    }

    [Fact]
    public async Task Update_AuditsChangesAndHistoricalRetryDoesNotUndoLaterWork()
    {
        var createKey = Guid.NewGuid();
        var created = await _store.CreateAsync(_scope, createKey, _plan);
        var id = Id(created);
        var editKey = Guid.NewGuid();
        var updatedPlan = _plan with { Notes = "First edit" };
        var edited = await _store.UpdateAsync(_scope, editKey, id, 1, updatedPlan);
        await _store.UpdateAsync(_scope, Guid.NewGuid(), id, 2, updatedPlan with { Notes = "Second edit" });
        Assert.Equal(edited, await _store.UpdateAsync(_scope, editKey, id, 1, updatedPlan));
        Assert.Equal(created, await _store.CreateAsync(_scope, createKey, _plan));
        Assert.Equal("Second edit", (await _store.GetAsync(_scope, id))!.Notes);
        await using var db = new OrderDbContext(_options);
        Assert.Equal(3, await db.AuditEntries.CountAsync());
        Assert.Equal(3, (await db.Orders.SingleAsync()).Version);
    }

    [Fact]
    public async Task ConcurrentEdits_OnlyOneWinsAndNoChangeSaveRetainsVersion()
    {
        var created = await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        var id = Id(created);
        async Task<string> Edit(string notes)
        {
            try
            {
                await _store.UpdateAsync(_scope, Guid.NewGuid(), id, 1, _plan with { Notes = notes });
                return "accepted";
            }
            catch (OrderPlanningException exception) { return exception.Code; }
        }

        var results = await Task.WhenAll(Edit("one"), Edit("two"));
        Assert.Contains("accepted", results);
        Assert.Contains("version_conflict", results);
        var current = (await _store.GetAsync(_scope, id))!;
        await _store.UpdateAsync(_scope, Guid.NewGuid(), id, 2, current.Plan());
        await using var db = new OrderDbContext(_options);
        Assert.Equal(2, (await db.Orders.SingleAsync()).Version);
        Assert.Equal(2, await db.AuditEntries.CountAsync());
        Assert.Equal(3, await db.CommandReceipts.CountAsync());
    }

    [Fact]
    public async Task AuthorizationAndCustomerEligibility_AreEnforcedIncludingRetries()
    {
        var key = Guid.NewGuid();
        var created = await _store.CreateAsync(_scope, key, _plan);
        await Error("access_denied", () => _store.CreateAsync(_scope with { TenantId = Guid.NewGuid() }, key, _plan));
        await Error("access_denied", () => _store.CreateAsync(_scope with { PlantId = Guid.NewGuid() }, key, _plan));
        await Error("access_denied", () => _store.CreateAsync(_scope with { ActorSubject = "gateway-machine" }, key, _plan));
        await Error("customer_not_eligible", () => _store.CreateAsync(_scope, Guid.NewGuid(), _plan with { CustomerId = Guid.NewGuid() }));
        await Error("customer_not_eligible", () => _store.UpdateAsync(_scope, Guid.NewGuid(), Id(created), 1, _plan with { CustomerId = Guid.NewGuid() }));
        _access.Allowed = false;
        await Error("access_denied", () => _store.CreateAsync(_scope, key, _plan));
    }

    [Fact]
    public async Task ReadAndUpdate_CannotFindOrderOutsideScope()
    {
        var created = await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        var other = _scope with { PlantId = Guid.NewGuid() };
        var otherAccess = new TestAccess(other, _plan.CustomerId);
        var otherStore = new OrderPlanningStore(_options, otherAccess, TimeProvider.System);
        Assert.Null(await otherStore.GetAsync(other, Id(created)));
        await Error("order_not_found", () => otherStore.UpdateAsync(other, Guid.NewGuid(), Id(created), 1, _plan));
    }

    [Fact]
    public async Task FailedReceiptInsert_RollsBackOrderAuditAndCounterThenRetrySucceeds()
    {
        await using var db = new OrderDbContext(_options);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.command_receipts ADD CONSTRAINT test_failure CHECK (response_status = 0)");
        var key = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateException>(() => _store.CreateAsync(_scope, key, _plan));
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(0, await db.AuditEntries.CountAsync());
        Assert.Equal(0, await db.NumberCounters.CountAsync());
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.command_receipts DROP CONSTRAINT test_failure");
        var saved = await _store.CreateAsync(_scope, key, _plan);
        Assert.Equal("ORD-000001", (await _store.GetAsync(_scope, Id(saved)))!.OrderNumber);
    }

    [Fact]
    public async Task HistoryAndReceipts_RejectSqlMutationAndScopedForeignKeyMismatch()
    {
        await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        await using var db = new OrderDbContext(_options);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM orders.order_audit_entries"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE orders.command_receipts SET response_status = 200"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE orders.order_audit_entries"));
        var receipt = await db.CommandReceipts.AsNoTracking().SingleAsync();
        receipt.PlantId = Guid.NewGuid();
        receipt.IdempotencyKey = Guid.NewGuid();
        db.CommandReceipts.Add(receipt);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    [Fact]
    public async Task DatabaseConstraints_RejectInvalidDateAndNoPendingModelChanges()
    {
        await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        await using var db = new OrderDbContext(_options);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE orders.service_orders SET planned_delivery_date = planned_receiving_date - 1"));
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.MigrateAsync();
        Assert.Equal(1, await db.Orders.CountAsync());
        // Order migrations neither own nor replace the existing scan schema/history.
        await using var scans = new ScanDbContext(new DbContextOptionsBuilder<ScanDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);
        await scans.Database.MigrateAsync();
        Assert.False(scans.Database.HasPendingModelChanges());
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task UnreachableDatabase_DoesNotReturnReceiptAndSameKeyCanRecover()
    {
        var key = Guid.NewGuid();
        var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Port = 1, Timeout = 1, Pooling = false };
        var unavailable = new OrderPlanningStore(Options(builder.ConnectionString), _access, TimeProvider.System);
        await Assert.ThrowsAsync<NpgsqlException>(() => unavailable.CreateAsync(_scope, key, _plan));
        var created = await _store.CreateAsync(_scope, key, _plan);
        Assert.Equal(created, await _store.CreateAsync(_scope, key, _plan));
    }

    private static DbContextOptions<OrderDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(connection,
            options => options.MigrationsHistoryTable("__order_migrations_history", "orders")).Options;

    private static Guid Id(OrderPlanningReceipt receipt)
    {
        using var json = JsonDocument.Parse(receipt.Body);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task Error(string code, Func<Task<OrderPlanningReceipt>> operation) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<OrderPlanningException>(operation)).Code);

    private sealed class TestAccess(OrderPlanningScope permittedScope, Guid customer) : IOrderPlanningAccess
    {
        public bool Allowed { get; set; } = true;
        public bool CanEditNotesDefault { get; set; }
        public Guid? AdditionalCustomer { get; set; }
        public Task<bool> IsAllowedAsync(OrderPlanningScope scope, string permission, CancellationToken cancellationToken) =>
            Task.FromResult(Allowed && scope == permittedScope &&
                (permission is "orders.create" or "orders.plan.edit" or "orders.read" ||
                 permission == OrderPlanningPermissions.EditNotesDefault && CanEditNotesDefault));
        public Task<bool> IsCustomerEligibleAsync(OrderPlanningScope scope, Guid customerId, CancellationToken cancellationToken) =>
            Task.FromResult(scope == permittedScope && (customerId == customer || customerId == AdditionalCustomer));
        public async Task<OrderPublicationCustomer?> ReadPublicationCustomerAsync(OrderPlanningScope scope, Guid customerId, CancellationToken cancellationToken) =>
            await IsCustomerEligibleAsync(scope, customerId, cancellationToken)
                ? new(scope.TenantId, customerId, "DEMO-001", "Synthetic customer") : null;
    }
}
