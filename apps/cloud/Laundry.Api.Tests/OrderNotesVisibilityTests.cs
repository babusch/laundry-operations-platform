using System.Text.Json;
using Laundry.Api.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Laundry.Api.Tests;

public sealed partial class OrderPlanningDatabaseTests
{
    [Fact]
    public async Task Notes_DefaultIsHiddenAndPerOrderChoiceOverridesEitherDirection()
    {
        Assert.Equal(new(false, 0), await _store.ReadNotesDefaultAsync(_scope));
        var hidden = await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        var visible = await _store.CreateAsync(_scope, Guid.NewGuid(), _plan, notesVisibleToOperators: true);
        Assert.False((await _store.GetAsync(_scope, Id(hidden)))!.NotesVisibleToOperators);
        Assert.True((await _store.GetAsync(_scope, Id(visible)))!.NotesVisibleToOperators);
        _access.CanEditNotesDefault = true;
        Assert.Equal(new(true, 1), await _store.ChangeNotesDefaultAsync(_scope, Guid.NewGuid(), 0, true));
        var inherited = await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        var explicitHidden = await _store.CreateAsync(_scope, Guid.NewGuid(), _plan, notesVisibleToOperators: false);
        Assert.True((await _store.GetAsync(_scope, Id(inherited)))!.NotesVisibleToOperators);
        Assert.False((await _store.GetAsync(_scope, Id(explicitHidden)))!.NotesVisibleToOperators);
        await _store.ChangeNotesDefaultAsync(_scope, Guid.NewGuid(), 1, false);
        Assert.True((await _store.GetAsync(_scope, Id(inherited)))!.NotesVisibleToOperators);
        Assert.False((await _store.GetAsync(_scope, Id(hidden)))!.NotesVisibleToOperators);
    }

    [Fact]
    public async Task Notes_OmittedEditPreservesChoiceAndExplicitChangesAreVersionedAndAudited()
    {
        var key = Guid.NewGuid();
        var created = await _store.CreateAsync(_scope, key, _plan with { Notes = "Management-only synthetic text" }, notesVisibleToOperators: true);
        var id = Id(created);
        var edited = _plan with { Notes = "Updated synthetic text" };
        await _store.UpdateAsync(_scope, Guid.NewGuid(), id, 1, edited);
        Assert.True((await _store.GetAsync(_scope, id))!.NotesVisibleToOperators);
        var hideKey = Guid.NewGuid();
        var hidden = await _store.UpdateAsync(_scope, hideKey, id, 2, edited, notesVisibleToOperators: false);
        var order = (await _store.GetAsync(_scope, id))!;
        Assert.False(order.NotesVisibleToOperators); Assert.Equal(3, order.Version);
        Assert.Equal(edited.Notes, order.Notes); // Visibility never deletes management notes.
        await using var db = new OrderDbContext(_options);
        var audit = await db.AuditEntries.SingleAsync(x => x.OrderVersion == 3);
        Assert.Equal(_scope.ActorSubject, audit.ActorSubject);
        Assert.Equal(hideKey, audit.RequestId);
        Assert.True(JsonDocument.Parse(audit.BeforePlan!).RootElement.GetProperty("notesVisibleToOperators").GetBoolean());
        Assert.False(JsonDocument.Parse(audit.AfterPlan).RootElement.GetProperty("notesVisibleToOperators").GetBoolean());
        await _store.UpdateAsync(_scope, Guid.NewGuid(), id, 3, edited, notesVisibleToOperators: true);
        Assert.Equal(hidden, await _store.UpdateAsync(_scope, hideKey, id, 2, edited, notesVisibleToOperators: false));
        Assert.True((await _store.GetAsync(_scope, id))!.NotesVisibleToOperators);
        await Error("version_conflict", () => _store.UpdateAsync(_scope, Guid.NewGuid(), id, 2, edited, notesVisibleToOperators: false));
        await Error("idempotency_conflict", () => _store.UpdateAsync(_scope, hideKey, id, 2, edited, notesVisibleToOperators: true));
        await _store.UpdateAsync(_scope, Guid.NewGuid(), id, 4, edited, notesVisibleToOperators: true);
        Assert.Equal(4, await db.AuditEntries.CountAsync());
        Assert.Equal(4, (await _store.GetAsync(_scope, id))!.Version);
    }

    [Fact]
    public async Task Notes_CreationRetryDoesNotReevaluateChangedDefault()
    {
        var key = Guid.NewGuid();
        var created = await _store.CreateAsync(_scope, key, _plan);
        _access.CanEditNotesDefault = true;
        await _store.ChangeNotesDefaultAsync(_scope, Guid.NewGuid(), 0, true);
        Assert.Equal(created, await _store.CreateAsync(_scope, key, _plan));
        Assert.False((await _store.GetAsync(_scope, Id(created)))!.NotesVisibleToOperators);
        await Error("idempotency_conflict", () => _store.CreateAsync(_scope, key, _plan, notesVisibleToOperators: false));
        var concurrentKey = Guid.NewGuid();
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _store.CreateAsync(_scope, concurrentKey, _plan)));
        Assert.All(duplicates, x => Assert.Equal(duplicates[0], x));
        Assert.True((await _store.GetAsync(_scope, Id(duplicates[0])))!.NotesVisibleToOperators);
    }

    [Fact]
    public async Task Notes_DefaultRequiresSeparatePermissionAndExactScopeEvenOnRetry()
    {
        var key = Guid.NewGuid();
        async Task Denied(Func<Task> operation) => Assert.Equal("access_denied", (await Assert.ThrowsAsync<OrderPlanningException>(operation)).Code);
        await Denied(() => _store.ChangeNotesDefaultAsync(_scope, key, 0, true));
        Assert.DoesNotContain(OrderPlanningPermissions.EditNotesDefault, OrderPlanningPermissions.Management);
        _access.CanEditNotesDefault = true;
        await _store.ChangeNotesDefaultAsync(_scope, key, 0, true);
        await Denied(() => _store.ReadNotesDefaultAsync(_scope with { TenantId = Guid.NewGuid() }));
        await Denied(() => _store.ChangeNotesDefaultAsync(_scope with { PlantId = Guid.NewGuid() }, key, 0, true));
        await Denied(() => _store.ChangeNotesDefaultAsync(_scope with { ActorSubject = "other" }, key, 0, true));
        _access.CanEditNotesDefault = false;
        await Denied(() => _store.ChangeNotesDefaultAsync(_scope, key, 0, true));
    }

    [Fact]
    public async Task Notes_DefaultConcurrentWritesRetriesAndNoChangeAreExplicit()
    {
        _access.CanEditNotesDefault = true;
        var key = Guid.NewGuid();
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _store.ChangeNotesDefaultAsync(_scope, key, 0, true)));
        Assert.All(duplicates, x => Assert.Equal(new(true, 1), x));
        async Task Code(string expected, Func<Task> operation) => Assert.Equal(expected, (await Assert.ThrowsAsync<OrderPlanningException>(operation)).Code);
        await Code("idempotency_conflict", () => _store.ChangeNotesDefaultAsync(_scope, key, 0, false));
        await Code("idempotency_conflict", () => _store.ChangeNotesDefaultAsync(_scope, key, 1, true));
        await Code("version_conflict", () => _store.ChangeNotesDefaultAsync(_scope, Guid.NewGuid(), 0, false));
        await Code("invalid_command", () => _store.ChangeNotesDefaultAsync(_scope, Guid.Empty, 1, false));
        await Code("invalid_command", () => _store.ChangeNotesDefaultAsync(_scope, Guid.NewGuid(), -1, false));
        Assert.Equal(new(true, 1), await _store.ChangeNotesDefaultAsync(_scope, Guid.NewGuid(), 1, true));
        Assert.Equal(new(false, 2), await _store.ChangeNotesDefaultAsync(_scope, Guid.NewGuid(), 1, false));
        Assert.Equal(new(true, 1), await _store.ChangeNotesDefaultAsync(_scope, key, 0, true));
        Assert.Equal(new(false, 2), await _store.ReadNotesDefaultAsync(_scope));
        await using var db = new OrderDbContext(_options);
        Assert.Equal(3, await db.NotesDefaultCommands.CountAsync());
        Assert.Empty(await db.Orders.ToArrayAsync());
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE orders.note_default_commands SET after_visible = false"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM orders.note_default_commands"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE orders.note_default_commands"));
    }

    [Fact]
    public async Task Notes_DefaultIsScopedAndConcurrentDistinctEditsCannotOverwrite()
    {
        _access.CanEditNotesDefault = true;
        await _store.ChangeNotesDefaultAsync(_scope, Guid.NewGuid(), 0, true);
        var otherScope = _scope with { PlantId = Guid.NewGuid() };
        var otherStore = new OrderPlanningStore(_options, new TestAccess(otherScope, _plan.CustomerId), TimeProvider.System);
        Assert.Equal(new(false, 0), await otherStore.ReadNotesDefaultAsync(otherScope));
        var otherOrder = await otherStore.CreateAsync(otherScope, Guid.NewGuid(), _plan);
        Assert.False((await otherStore.GetAsync(otherScope, Id(otherOrder)))!.NotesVisibleToOperators);
        async Task<string> Change()
        {
            try { await _store.ChangeNotesDefaultAsync(_scope, Guid.NewGuid(), 1, false); return "accepted"; }
            catch (OrderPlanningException exception) { return exception.Code; }
        }
        var results = await Task.WhenAll(Change(), Change());
        Assert.Single(results, x => x == "accepted");
        Assert.Single(results, x => x == "version_conflict");
        var created = await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        await using var db = new OrderDbContext(_options);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.service_orders DROP CONSTRAINT ck_order_status");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders.service_orders SET status = 'non-planned-test-state' WHERE id = {Id(created)}");
        await Error("order_not_planned", () => _store.UpdateAsync(_scope, Guid.NewGuid(), Id(created), 1, _plan, notesVisibleToOperators: true));
    }

    [Fact]
    public async Task Notes_DefaultDatabaseOutageMakesNoPartialWriteAndSameKeyRecovers()
    {
        _access.CanEditNotesDefault = true;
        var unavailable = new OrderPlanningStore(Options("Host=localhost;Port=1;Database=synthetic;Username=synthetic;Timeout=1"), _access, TimeProvider.System);
        var key = Guid.NewGuid();
        await Assert.ThrowsAsync<NpgsqlException>(() => unavailable.ChangeNotesDefaultAsync(_scope, key, 0, true));
        Assert.Equal(new(false, 0), await _store.ReadNotesDefaultAsync(_scope));
        Assert.Equal(new(true, 1), await _store.ChangeNotesDefaultAsync(_scope, key, 0, true));
        Assert.Equal(new(true, 1), await _store.ChangeNotesDefaultAsync(_scope, key, 0, true));
        await using var db = new OrderDbContext(_options);
        Assert.Single(await db.NotesDefaultCommands.ToArrayAsync());
    }

    [Fact]
    public async Task Notes_MigrationHidesLegacyOrdersWithoutRewritingEvidenceOrBreakingRetry()
    {
        await using var db = new OrderDbContext(_options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261015173629_OrderAuditAccountName");
        var id = Guid.NewGuid(); var key = Guid.NewGuid(); var auditId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var plan = _plan with { Notes = "Synthetic legacy note" };
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var evidence = JsonSerializer.Serialize(plan, jsonOptions);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { operation = "create", target = "orders", expectedVersion = (long?)null, plan }, jsonOptions))));
        var body = JsonSerializer.Serialize(new { id });
        var etag = $"\"{id:D}:1\"";
        var location = $"/api/v1/plants/{_scope.PlantId:D}/orders/{id:D}";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO orders.service_orders (id,tenant_id,plant_id,customer_id,order_number,planned_receiving_date,planned_delivery_date,notes,status,version,created_at_utc,updated_at_utc,created_by_issuer,created_by_subject,updated_by_issuer,updated_by_subject) VALUES ({id},{_scope.TenantId},{_scope.PlantId},{plan.CustomerId},'SYNTHETIC-LEGACY',{plan.PlannedReceivingDate},{plan.PlannedDeliveryDate},{plan.Notes},'planned',1,{now},{now},{_scope.ActorIssuer},{_scope.ActorSubject},{_scope.ActorIssuer},{_scope.ActorSubject})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO orders.order_audit_entries (id,tenant_id,plant_id,order_id,action,order_version,recorded_at_utc,actor_issuer,actor_subject,request_id,after_plan) VALUES ({auditId},{_scope.TenantId},{_scope.PlantId},{id},'order_created',1,{now},{_scope.ActorIssuer},{_scope.ActorSubject},{key},{evidence}::jsonb)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO orders.command_receipts (tenant_id,plant_id,actor_issuer,actor_subject,idempotency_key,operation,target,request_fingerprint,order_id,response_status,response_body,response_etag,response_location,accepted_at_utc) VALUES ({_scope.TenantId},{_scope.PlantId},{_scope.ActorIssuer},{_scope.ActorSubject},{key},'create','orders',{fingerprint},{id},201,{body}::jsonb,{etag},{location},{now})");
        var originalAudit = (await db.AuditEntries.AsNoTracking().SingleAsync()).AfterPlan;
        var originalReceipt = (await db.CommandReceipts.AsNoTracking().SingleAsync()).Result();
        await migrator.MigrateAsync();
        var order = (await _store.GetAsync(_scope, id))!;
        Assert.False(order.NotesVisibleToOperators); Assert.Equal(plan.Notes, order.Notes); Assert.Equal(1, order.Version);
        Assert.Equal(originalAudit, (await db.AuditEntries.AsNoTracking().SingleAsync()).AfterPlan);
        Assert.Equal(originalReceipt, await _store.CreateAsync(_scope, key, plan));
        Assert.Empty(await db.NotesDefaults.ToArrayAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Notes_DefaultAuditFailureRollsBackAndExactRetryCanRecover()
    {
        _access.CanEditNotesDefault = true;
        await using var db = new OrderDbContext(_options);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.note_default_commands ADD CONSTRAINT test_fail CHECK (result_version = 0)");
        var key = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateException>(() => _store.ChangeNotesDefaultAsync(_scope, key, 0, true));
        Assert.Empty(await db.NotesDefaults.ToArrayAsync());
        Assert.Empty(await db.NotesDefaultCommands.ToArrayAsync());
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.note_default_commands DROP CONSTRAINT test_fail");
        Assert.Equal(new(true, 1), await _store.ChangeNotesDefaultAsync(_scope, key, 0, true));
    }
}
