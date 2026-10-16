using System.Text.Json;
using Laundry.Api.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Laundry.Api.Tests;

public sealed partial class OrderPlanningDatabaseTests
{
    private OrderPublicationCustomer PublicationCustomer(string name = "Synthetic customer") =>
        new(_scope.TenantId, _plan.CustomerId, "DEMO-001", name);

    // Model a pre-publication installation without bypass switches in production commands.
    private async Task<OrderPlanningReceipt> SeedUnpublishedOrderAsync(OrderPlan? plan = null, bool visible = false)
    {
        plan ??= _plan;
        await using var db = new OrderDbContext(_options);
        var now = DateTimeOffset.UtcNow; var key = Guid.NewGuid();
        var order = new ServiceOrder
        {
            Id = Guid.NewGuid(),
            TenantId = _scope.TenantId,
            PlantId = _scope.PlantId,
            OrderNumber = "ORD-000001",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByIssuer = _scope.ActorIssuer,
            UpdatedByIssuer = _scope.ActorIssuer,
            CreatedBySubject = _scope.ActorSubject,
            UpdatedBySubject = _scope.ActorSubject,
            NotesVisibleToOperators = visible
        };
        order.Apply(plan);
        db.Orders.Add(order);
        var body = JsonSerializer.Serialize(OrderDetails.From(order), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        db.AuditEntries.Add(new()
        {
            Id = Guid.NewGuid(),
            TenantId = _scope.TenantId,
            PlantId = _scope.PlantId,
            OrderId = order.Id,
            Action = "order_created",
            OrderVersion = 1,
            RecordedAtUtc = now,
            ActorIssuer = _scope.ActorIssuer,
            ActorSubject = _scope.ActorSubject,
            RequestId = key,
            AfterPlan = JsonSerializer.Serialize(plan)
        });
        var receipt = new OrderCommandReceipt
        {
            TenantId = _scope.TenantId,
            PlantId = _scope.PlantId,
            ActorIssuer = _scope.ActorIssuer,
            ActorSubject = _scope.ActorSubject,
            IdempotencyKey = key,
            OrderId = order.Id,
            Operation = "create",
            Target = "orders",
            RequestFingerprint = "synthetic-legacy-fixture",
            ResponseStatus = 201,
            ResponseBody = body,
            ResponseEtag = OrderDetails.From(order).EntityTag(),
            ResponseLocation = $"/api/v1/plants/{_scope.PlantId:D}/orders/{order.Id:D}",
            AcceptedAtUtc = now
        };
        db.CommandReceipts.Add(receipt); await db.SaveChangesAsync(); return receipt.Result();
    }

    private async Task<OrderPublicationStage> StagePublication(Guid id, string name = "Synthetic customer", bool initial = false,
        Action<ServiceOrder>? edit = null, DbContextOptions<OrderDbContext>? options = null)
    {
        await using var db = new OrderDbContext(options ?? _options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await OrderPublicationStorage.LockPlantAsync(db, _scope.TenantId, _scope.PlantId);
        var order = await db.Orders.SingleAsync(x => x.Id == id && x.TenantId == _scope.TenantId && x.PlantId == _scope.PlantId);
        if (edit is not null) { edit(order); order.Version++; }
        var result = await OrderPublicationStorage.StageAsync(db, _scope.TenantId, _scope.PlantId, order, PublicationCustomer(name), TimeProvider.System, initial);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    [Fact]
    public async Task Publication_InitialMissingOnlyIsRepeatableWithoutChangingOrdersOrHistory()
    {
        var created = await SeedUnpublishedOrderAsync(_plan with { Notes = "Synthetic hidden note" });
        var id = Id(created);
        Assert.Equal(new(1, true), await StagePublication(id, initial: true));
        Assert.Equal(new(1, false), await StagePublication(id, initial: true));
        Assert.Equal(new(1, false), await StagePublication(id, "A newer label must not alter initial-only copies", initial: true));
        await using var db = new OrderDbContext(_options);
        var head = await db.PublicationHeads.SingleAsync(); var outbox = await db.PublicationOutbox.SingleAsync();
        Assert.Equal(head.Payload, outbox.Payload); Assert.Equal(1, outbox.Version); Assert.NotEqual(Guid.Empty, outbox.Id);
        Assert.DoesNotContain("Synthetic hidden note", head.Payload);
        using var json = JsonDocument.Parse(head.Payload); Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("notes").ValueKind);
        Assert.Equal(1, (await db.Orders.SingleAsync()).Version);
        Assert.Equal(1, await db.AuditEntries.CountAsync()); Assert.Equal(1, await db.CommandReceipts.CountAsync());
    }

    [Fact]
    public async Task Publication_ChangesRevisionOnlyWhenPublishedContentChanges()
    {
        var created = await SeedUnpublishedOrderAsync(_plan with { Notes = "Synthetic visible" }, visible: true);
        var id = Id(created);
        Assert.Equal(new(1, true), await StagePublication(id));
        Assert.Equal(new(1, false), await StagePublication(id, edit: o => o.CustomerReference = "Management-only edit"));
        Assert.Equal(new(2, true), await StagePublication(id, edit: o => o.NotesVisibleToOperators = false));
        Assert.Equal(new(2, false), await StagePublication(id, edit: o => o.Notes = "Another hidden management note"));
        Assert.Equal(new(3, true), await StagePublication(id, "Synthetic renamed customer"));
        await using var db = new OrderDbContext(_options);
        var current = await db.PublicationHeads.SingleAsync(); Assert.Equal(3, current.Version);
        Assert.DoesNotContain("Another hidden management note", current.Payload);
        Assert.DoesNotContain("Synthetic visible", current.Payload);
        var rows = await db.PublicationOutbox.OrderBy(x => x.Version).ToArrayAsync(); Assert.Equal(3, rows.Length);
        Assert.Contains("Synthetic visible", rows[0].Payload); // Cloud immutable evidence, not a gateway working copy.
        Assert.DoesNotContain("Synthetic visible", rows[1].Payload);
        Assert.Contains("Synthetic renamed customer", rows[2].Payload);
    }

    [Fact]
    public async Task Publication_ConcurrentDuplicateAndDistinctUpdatesSerializeWithoutRevisionGaps()
    {
        var id = Id(await SeedUnpublishedOrderAsync());
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => StagePublication(id)));
        Assert.Single(duplicates, x => x.Changed); Assert.All(duplicates, x => Assert.Equal(1, x.Version));
        await Task.WhenAll(StagePublication(id, "Synthetic label A"), StagePublication(id, "Synthetic label B"));
        await using var db = new OrderDbContext(_options);
        Assert.Equal(new long[] { 1, 2, 3 }, await db.PublicationOutbox.OrderBy(x => x.Version).Select(x => x.Version).ToArrayAsync());
        Assert.Equal(3, (await db.PublicationHeads.SingleAsync()).Version);
    }

    [Fact]
    public async Task Publication_OrderHeadAndOutboxRollbackTogetherOnFailure()
    {
        var id = Id(await SeedUnpublishedOrderAsync());
        var failing = new DbContextOptionsBuilder<OrderDbContext>(_options).AddInterceptors(new RejectPublicationSave()).Options;
        await Assert.ThrowsAsync<InvalidOperationException>(() => StagePublication(id, edit: o => o.Notes = "Uncommitted edit", options: failing));
        await using var db = new OrderDbContext(_options);
        Assert.Empty(await db.PublicationHeads.ToArrayAsync()); Assert.Empty(await db.PublicationOutbox.ToArrayAsync());
        Assert.Null((await db.Orders.SingleAsync()).Notes); Assert.Equal(1, (await db.Orders.SingleAsync()).Version);
        Assert.Equal(new(1, true), await StagePublication(id));
    }

    [Fact]
    public async Task Publication_LostAcknowledgementRetryPreservesOriginalOutboxIdentityAndBytes()
    {
        var id = Id(await SeedUnpublishedOrderAsync());
        var options = new DbContextOptionsBuilder<OrderDbContext>(_options).AddInterceptors(new LosePublicationCommitAcknowledgement()).Options;
        await Assert.ThrowsAsync<TimeoutException>(() => StagePublication(id, options: options));
        await using var before = new OrderDbContext(_options);
        var original = await before.PublicationOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(new(1, false), await StagePublication(id));
        await using var after = new OrderDbContext(_options); var retried = await after.PublicationOutbox.SingleAsync();
        Assert.Equal(original.Id, retried.Id); Assert.Equal(original.Payload, retried.Payload);
        Assert.Equal(original.RecordedAtUtc, retried.RecordedAtUtc);
    }

    [Fact]
    public async Task Publication_SurvivesDatabaseRestartAndIsSafeToRepeat()
    {
        var id = Id(await SeedUnpublishedOrderAsync());
        await StagePublication(id);
        await _postgres.StopAsync(); await _postgres.StartAsync();
        var restarted = Options(_postgres.GetConnectionString());
        Assert.Equal(new(1, false), await StagePublication(id, options: restarted));
        await using var db = new OrderDbContext(restarted); Assert.Single(await db.PublicationOutbox.ToArrayAsync());
    }

    [Fact]
    public async Task Publication_RequiresTransactionTrackedCurrentOrderAndMatchingScope()
    {
        var id = Id(await SeedUnpublishedOrderAsync());
        await using var db = new OrderDbContext(_options); var order = await db.Orders.SingleAsync();
        Assert.Equal("publication_requires_transaction", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OrderPublicationStorage.StageAsync(db, _scope.TenantId, _scope.PlantId, order, PublicationCustomer(), TimeProvider.System))).Message);
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.Equal("order_publication_scope_mismatch", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OrderPublicationStorage.StageAsync(db, Guid.NewGuid(), _scope.PlantId, order, PublicationCustomer(), TimeProvider.System))).Message);
        await _store.UpdateAsync(_scope, Guid.NewGuid(), id, 1, _plan with { CustomerReference = "Concurrent change" });
        Assert.Equal("publication_stale_order", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OrderPublicationStorage.StageAsync(db, _scope.TenantId, _scope.PlantId, order, PublicationCustomer(), TimeProvider.System))).Message);
    }

    [Fact]
    public async Task Publication_OutboxIsAppendOnlyInEfAndSql()
    {
        var id = Id(await SeedUnpublishedOrderAsync()); await StagePublication(id);
        await using var db = new OrderDbContext(_options); var row = await db.PublicationOutbox.SingleAsync();
        row.Payload = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        foreach (var sql in new[] { "UPDATE orders.publication_outbox SET payload = 'tampered'", "DELETE FROM orders.publication_outbox", "TRUNCATE orders.publication_outbox" })
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
    }

    [Fact]
    public async Task Publication_RevisionOverflowRollsBackAndLeavesExistingCopyUntouched()
    {
        var id = Id(await SeedUnpublishedOrderAsync()); await StagePublication(id);
        await using (var db = new OrderDbContext(_options))
            await db.Database.ExecuteSqlRawAsync("UPDATE orders.publication_heads SET version = 9007199254740991");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => StagePublication(id, "Synthetic newer name"));
        await using var check = new OrderDbContext(_options);
        Assert.Single(await check.PublicationOutbox.ToArrayAsync()); Assert.Equal(9007199254740991, (await check.PublicationHeads.SingleAsync()).Version);
    }

    [Fact]
    public async Task Publication_NewOrderCanCommitWithItsFirstCopyAndSendRecord()
    {
        await using var db = new OrderDbContext(_options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await OrderPublicationStorage.LockPlantAsync(db, _scope.TenantId, _scope.PlantId);
        var order = new ServiceOrder
        {
            Id = Guid.NewGuid(),
            TenantId = _scope.TenantId,
            PlantId = _scope.PlantId,
            OrderNumber = "SYNTHETIC-ATOMIC-ORDER",
            CustomerId = _plan.CustomerId,
            PlannedReceivingDate = _plan.PlannedReceivingDate,
            PlannedDeliveryDate = _plan.PlannedDeliveryDate,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        db.Orders.Add(order);
        Assert.Equal(new(1, true), await OrderPublicationStorage.StageAsync(db, _scope.TenantId, _scope.PlantId,
            order, PublicationCustomer(), TimeProvider.System));
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        await using var check = new OrderDbContext(_options);
        Assert.Single(await check.Orders.ToArrayAsync()); Assert.Single(await check.PublicationHeads.ToArrayAsync());
        Assert.Single(await check.PublicationOutbox.ToArrayAsync());
    }

    [Fact]
    public async Task Publication_DetachedInputAndCrossScopeForeignKeysAreRejected()
    {
        await SeedUnpublishedOrderAsync();
        await using var db = new OrderDbContext(_options);
        var detached = await db.Orders.AsNoTracking().SingleAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.Equal("publication_requires_tracked_order", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OrderPublicationStorage.StageAsync(db, _scope.TenantId, _scope.PlantId, detached, PublicationCustomer(), TimeProvider.System))).Message);
        db.PublicationHeads.Add(new()
        {
            TenantId = Guid.NewGuid(),
            PlantId = _scope.PlantId,
            OrderId = detached.Id,
            Version = 1,
            ContentFingerprint = "synthetic",
            Payload = "{}"
        });
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    [Fact]
    public async Task Publication_MigrationAddsEmptyTablesWithoutChangingExistingOrderEvidence()
    {
        await using var db = new OrderDbContext(_options);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261015184801_OrderNotesVisibility");
        await SeedUnpublishedOrderAsync(_plan with { Notes = "Synthetic preserved management text" });
        var before = JsonSerializer.Serialize(await db.Orders.AsNoTracking().ToArrayAsync());
        var audits = JsonSerializer.Serialize(await db.AuditEntries.AsNoTracking().ToArrayAsync());
        var receipts = JsonSerializer.Serialize(await db.CommandReceipts.AsNoTracking().ToArrayAsync());
        await migrator.MigrateAsync();
        Assert.Equal(before, JsonSerializer.Serialize(await db.Orders.AsNoTracking().ToArrayAsync()));
        Assert.Equal(audits, JsonSerializer.Serialize(await db.AuditEntries.AsNoTracking().ToArrayAsync()));
        Assert.Equal(receipts, JsonSerializer.Serialize(await db.CommandReceipts.AsNoTracking().ToArrayAsync()));
        Assert.Empty(await db.PublicationHeads.ToArrayAsync()); Assert.Empty(await db.PublicationOutbox.ToArrayAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private sealed class RejectPublicationSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic_save_failure");
    }

    private sealed class LosePublicationCommitAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default) => throw new TimeoutException("synthetic_lost_commit_acknowledgement");
    }
}
