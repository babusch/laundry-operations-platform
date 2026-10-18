using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Laundry.Edge.Orders;
using Laundry.Edge.Persistence;
using Laundry.Edge.Scans;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Laundry.Edge.Tests;

public sealed class OrderStorageFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:18.6-alpine3.23").Build();
    public DbContextOptions<PlantDbContext> Options => MakeOptions(Postgres.GetConnectionString());
    public static DbContextOptions<PlantDbContext> MakeOptions(string connection) => new DbContextOptionsBuilder<PlantDbContext>().UseNpgsql(connection).Options;
    public async Task InitializeAsync()
    {
        await Postgres.StartAsync();
        await using var db = new PlantDbContext(Options); await db.Database.MigrateAsync();
    }
    public async Task DisposeAsync() => await Postgres.DisposeAsync();
}

[Trait("Category", "Database")]
public sealed class OrderDownloadStorageTests(OrderStorageFixture fixture) : IClassFixture<OrderStorageFixture>
{
    private readonly OrderDownloadScope _scope = new(Guid.NewGuid(), Guid.NewGuid());
    private readonly FixedOrderClock _clock = new();
    private OrderDownloadStore Store => new(fixture.Options, _scope, _clock);
    private JsonObject Order(long version = 1, Guid? id = null, bool visible = true)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "order-plan.v1.visible.json")))!.AsObject();
        json["tenantId"] = _scope.TenantId; json["plantId"] = _scope.PlantId;
        json["orderId"] = id ?? Guid.NewGuid(); json["version"] = version;
        json["notesVisibleToOperators"] = visible;
        if (!visible) json["notes"] = null;
        return json;
    }
    private static string Batch(string cursor, params JsonObject[] items) =>
        new JsonObject { ["items"] = new JsonArray(items.Cast<JsonNode>().ToArray()), ["nextCursor"] = cursor, ["hasMore"] = false }.ToJsonString();
    private Task<OrderBatchResult> Apply(string json, long revision = 0, string? cursor = null, Guid? id = null) =>
        Store.ApplyAsync(id ?? Guid.NewGuid(), revision, cursor, json);
    private async Task<LocalOrderPlan[]> Copies()
    {
        await using var db = new PlantDbContext(fixture.Options);
        return await db.LocalOrderPlans.AsNoTracking().Where(x => x.TenantId == _scope.TenantId && x.PlantId == _scope.PlantId).OrderBy(x => x.OrderId).ToArrayAsync();
    }
    private static async Task Error(string code, Func<Task<OrderBatchResult>> action) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<OrderDownloadException>(action)).Code);

    [Fact]
    public async Task OrderStorage_NewBatchPersistsCopiesProgressAndPayloadFreeReceiptTogether()
    {
        Assert.Equal(new(0, null, null), await Store.ReadProgressAsync());
        var accepted = await Apply(Batch("bookmark-1", Order(), Order(visible: false)));
        Assert.Equal(2, accepted.Applied); Assert.Equal(1, accepted.ProgressRevision);
        Assert.Equal(new(1, "bookmark-1", _clock.Now), await Store.ReadProgressAsync());
        Assert.Equal(2, (await Copies()).Length);
        await using var db = new PlantDbContext(fixture.Options);
        var receipt = await db.OrderDownloadReceipts.SingleAsync(x => x.BatchId == accepted.BatchId);
        Assert.Equal(64, receipt.RequestFingerprint.Length);
        Assert.DoesNotContain("instruction", JsonSerializer.Serialize(receipt));
    }

    [Fact]
    public async Task OrderStorage_ExactLostAcknowledgementRetryReturnsReceiptWithoutTouchingProgress()
    {
        var id = Guid.NewGuid(); var json = Batch("first", Order());
        var failing = new DbContextOptionsBuilder<PlantDbContext>(fixture.Options).AddInterceptors(new LostOrderCommit()).Options;
        await Assert.ThrowsAsync<TimeoutException>(() => new OrderDownloadStore(failing, _scope, _clock).ApplyAsync(id, 0, null, json));
        var saved = await Store.ReadProgressAsync(); _clock.Now = _clock.Now.AddDays(1);
        var replay = await Apply(json, id: id);
        Assert.Equal(id, replay.BatchId); Assert.Equal(saved, await Store.ReadProgressAsync());
        Assert.Single(await Copies());
        await Error("order_batch_id_conflict", () => Apply(Batch("changed"), id: id));
    }

    [Fact]
    public async Task OrderStorage_OlderReceiptCannotRewindLaterProgress()
    {
        var id = Guid.NewGuid(); var first = Batch("first", Order());
        var original = await Apply(first, id: id);
        await Apply(Batch("second"), 1, "first");
        Assert.Equal(original, await Apply(first, id: id));
        Assert.Equal(2, (await Store.ReadProgressAsync()).Revision);
        Assert.Equal("second", (await Store.ReadProgressAsync()).Cursor);
    }

    [Fact]
    public async Task OrderStorage_NewerHiddenCopyClearsTextAndOlderVisibleCopyCannotRestoreIt()
    {
        var order = Order(); var id = order["orderId"]!.GetValue<Guid>();
        await Apply(Batch("first", order));
        var hidden = await Apply(Batch("hidden", Order(2, id, visible: false)), 1, "first");
        Assert.Equal(1, hidden.Applied);
        var cleared = (await Copies()).Single();
        using (var json = JsonDocument.Parse(cleared.Payload)) Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("notes").ValueKind);
        var older = await Apply(Batch("older", Order(1, id)), 2, "hidden");
        Assert.Equal(1, older.Older); Assert.Equal(cleared.Payload, (await Copies()).Single().Payload);
    }

    [Fact]
    public async Task OrderStorage_SameRevisionDifferentContentRollsBackWholeBatch()
    {
        var existing = Order(); var id = existing["orderId"]!.GetValue<Guid>();
        await Apply(Batch("first", existing));
        var conflict = Order(1, id); conflict["orderNumber"] = "SYNTHETIC-CONFLICT";
        await Error("order_publication_revision_conflict", () => Apply(Batch("must-not-save", Order(), conflict), 1, "first"));
        Assert.Single(await Copies()); Assert.Equal("first", (await Store.ReadProgressAsync()).Cursor);
    }

    [Fact]
    public async Task OrderStorage_PropertyOrderEscapesAndIntegralNumbersAreNotContentConflicts()
    {
        var item = Order(); var id = item["orderId"]!.GetValue<Guid>();
        item["orderNumber"] = "SYNTHETIC-Ä";
        var original = Batch("first", item);
        await Apply(original);
        var copy = Order(1, id);
        copy["orderNumber"] = "SYNTHETIC-Ä";
        copy["tenantId"] = _scope.TenantId.ToString().ToUpperInvariant();
        copy["orderId"] = id.ToString().ToUpperInvariant();
        var reordered = new JsonObject(copy.Reverse().Select(x => new KeyValuePair<string, JsonNode?>(x.Key, x.Value?.DeepClone())));
        var incoming = Batch("second", reordered).Replace("\"version\":1", "\"version\":1.0", StringComparison.Ordinal)
            .Replace("\\u00C4", "Ä", StringComparison.Ordinal);
        var receipt = await Apply(incoming, 1, "first");
        Assert.Equal(1, receipt.Duplicates); Assert.Equal(0, receipt.Applied);
    }

    [Fact]
    public async Task OrderStorage_ConcurrentDuplicateBatchAppliesOnlyOnce()
    {
        var id = Guid.NewGuid(); var batch = Batch("first", Order());
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Apply(batch, id: id)));
        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal(1, (await Store.ReadProgressAsync()).Revision); Assert.Single(await Copies());
    }

    [Fact]
    public async Task OrderStorage_OverlappingResponsesCannotAdvanceAnOldBookmark()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async index =>
        {
            try { await Apply(Batch("bookmark-" + index, Order())); return "accepted"; }
            catch (OrderDownloadException ex) { return ex.Code; }
        }));
        Assert.Contains("accepted", results); Assert.Contains("stale_order_download_progress", results);
        Assert.Single(await Copies()); Assert.Equal(1, (await Store.ReadProgressAsync()).Revision);
    }

    [Fact]
    public async Task OrderStorage_SaveFailureRollsBackCopiesReceiptAndBookmark()
    {
        var failing = new DbContextOptionsBuilder<PlantDbContext>(fixture.Options).AddInterceptors(new FailOrderSave()).Options;
        var id = Guid.NewGuid(); var json = Batch("first", Order());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OrderDownloadStore(failing, _scope, _clock).ApplyAsync(id, 0, null, json));
        Assert.Empty(await Copies()); Assert.Equal(0, (await Store.ReadProgressAsync()).Revision);
        await using (var db = new PlantDbContext(fixture.Options)) Assert.False(await db.OrderDownloadReceipts.AnyAsync(x => x.BatchId == id));
        Assert.Equal(1, (await Apply(json, id: id)).Applied);
    }

    [Fact]
    public async Task OrderStorage_ScopeMismatchIncludingMixedBatchHasNoWrites()
    {
        foreach (var field in new[] { "tenantId", "plantId" })
        {
            var bad = Order(); bad[field] = Guid.NewGuid();
            await Error("order_download_scope_mismatch", () => Apply(Batch("bad", Order(), bad)));
        }
        Assert.Empty(await Copies()); Assert.Equal(0, (await Store.ReadProgressAsync()).Revision);
        var receipt = await Apply(Batch("first", Order()));
        var other = new OrderDownloadStore(fixture.Options, new(_scope.TenantId, Guid.NewGuid()), _clock);
        await Error("order_batch_id_conflict", () => other.ApplyAsync(receipt.BatchId, 0, null, Batch("other")));
        Assert.Equal(0, (await other.ReadProgressAsync()).Revision);
    }

    [Fact]
    public async Task OrderStorage_EmptyPageUpdatesCheckTimeWithoutRemovingCopies()
    {
        await Apply(Batch("first", Order())); _clock.Now = _clock.Now.AddHours(1);
        var empty = await Apply(Batch("caught-up"), 1, "first");
        Assert.Equal(0, empty.Applied); Assert.Single(await Copies());
        Assert.Equal(_clock.Now, (await Store.ReadProgressAsync()).LastSuccessfulCheckUtc);
        await Error("stale_order_download_progress", () => Apply(Batch("wrong"), 2, "not-the-saved-cursor"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not JSON")]
    [InlineData("{\"items\":[],\"nextCursor\":\"a\",\"hasMore\":true}")]
    [InlineData("{\"items\":[],\"nextCursor\":\"\",\"hasMore\":false}")]
    [InlineData("{\"items\":[],\"nextCursor\":\"a\",\"hasMore\":false,\"unknown\":1}")]
    [InlineData("{\"items\":[],\"nextCursor\":\"a\",\"nextCursor\":\"b\",\"hasMore\":false}")]
    public async Task OrderStorage_RejectsMalformedBatch(string json)
    {
        await Error("invalid_order_batch", () => Apply(json));
        Assert.Equal(0, (await Store.ReadProgressAsync()).Revision);
    }

    [Fact]
    public async Task OrderStorage_ContractPrivacyDatesAndDuplicateIdsAreValidatedBeforeSaving()
    {
        foreach (var mutate in new Action<JsonObject>[] {
            x => x["notesVisibleToOperators"] = false, x => x["customerReference"] = "MUST-NOT-STORE",
            x => x["version"] = 0, x => x["schemaVersion"] = 2, x => x["status"] = "completed",
            x => x["customer"]!["id"] = Guid.Empty, x => x["plannedDeliveryDate"] = "2000-01-01" })
        {
            var invalid = Order(); mutate(invalid);
            await Assert.ThrowsAsync<OrderDownloadException>(() => Apply(Batch("bad", invalid)));
        }
        var item = Order();
        await Error("duplicate_order_in_batch", () => Apply(Batch("duplicate", item, item.DeepClone().AsObject())));
        var duplicate = Batch("duplicate-property", Order()).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        await Error("invalid_order_batch", () => Apply(duplicate));
        Assert.Empty(await Copies());
    }

    [Fact]
    public async Task OrderStorage_BoundsCommandsAndProgressExhaustionFailWithoutMutation()
    {
        await Error("invalid_order_batch", () => Apply(Batch("oversized", Enumerable.Range(0, 101).Select(_ => Order()).ToArray())));
        await Error("invalid_order_batch", () => Apply(Batch(new string('x', 2049))));
        await Error("invalid_order_batch", () => Apply(new string('x', 4 * 1024 * 1024 + 1)));
        await Error("invalid_order_batch_command", () => Apply(Batch("empty"), id: Guid.Empty));
        await Error("invalid_order_batch_command", () => Apply(Batch("empty"), revision: -1));
        await Error("invalid_order_batch_command", () => Apply(Batch("empty"), cursor: " "));
        await Apply(Batch("first", Order()));
        await using (var db = new PlantDbContext(fixture.Options))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plant.order_download_progress SET revision={long.MaxValue} WHERE tenant_id={_scope.TenantId} AND plant_id={_scope.PlantId}");
        await Error("order_download_progress_exhausted", () => Apply(Batch("not-saved"), long.MaxValue, "first"));
        Assert.Single(await Copies()); Assert.Equal("first", (await Store.ReadProgressAsync()).Cursor);
    }

    [Fact]
    public async Task OrderStorage_RestartAndStorageOutagePreserveProgressAndCopies()
    {
        var id = Guid.NewGuid(); var json = Batch("first", Order());
        var accepted = await Apply(json, id: id);
        await fixture.Postgres.StopAsync(); await fixture.Postgres.StartAsync();
        Assert.Equal(accepted, await Apply(json, id: id)); Assert.Single(await Copies());
        var badConnection = new NpgsqlConnectionStringBuilder(fixture.Postgres.GetConnectionString()) { Port = 1, Pooling = false, Timeout = 1 };
        var down = new OrderDownloadStore(OrderStorageFixture.MakeOptions(badConnection.ConnectionString), _scope, _clock);
        await Assert.ThrowsAsync<NpgsqlException>(() => down.ApplyAsync(Guid.NewGuid(), 1, "first", Batch("not-saved")));
        Assert.Equal("first", (await Store.ReadProgressAsync()).Cursor);
    }

    [Fact]
    public async Task OrderStorage_ReceiptsRejectEfAndSqlMutation()
    {
        var accepted = await Apply(Batch("first", Order()));
        await using var db = new PlantDbContext(fixture.Options);
        var receipt = await db.OrderDownloadReceipts.SingleAsync(x => x.BatchId == accepted.BatchId); receipt.Applied++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => db.SaveChanges()));
        foreach (var sql in new[] { "UPDATE plant.order_download_receipts SET applied=0", "DELETE FROM plant.order_download_receipts", "TRUNCATE plant.order_download_receipts" })
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
    }

    [Fact]
    public async Task OrderStorage_MigrationAddsOnlyEmptyOrderTablesAndPreservesScans()
    {
        await using var db = new PlantDbContext(fixture.Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260920140054_TrustedSourceSecurityFoundation");
        var id = Guid.NewGuid();
        db.Observations.Add(new() { EventId = id, TenantId = _scope.TenantId, PlantId = _scope.PlantId, AcceptedAtUtc = _clock.Now, SubmissionJson = "{}", EventJson = "{}" });
        db.Outbox.Add(new() { EventId = id, Status = "pending" }); await db.SaveChangesAsync();
        var before = JsonSerializer.Serialize(await db.Observations.AsNoTracking().SingleAsync(x => x.EventId == id));
        var queue = JsonSerializer.Serialize(await db.Outbox.AsNoTracking().SingleAsync(x => x.EventId == id));
        await migrator.MigrateAsync();
        Assert.Equal(before, JsonSerializer.Serialize(await db.Observations.AsNoTracking().SingleAsync(x => x.EventId == id)));
        Assert.Equal(queue, JsonSerializer.Serialize(await db.Outbox.AsNoTracking().SingleAsync(x => x.EventId == id)));
        Assert.Empty(await db.LocalOrderPlans.ToArrayAsync()); Assert.Empty(await db.OrderDownloadProgress.ToArrayAsync()); Assert.Empty(await db.OrderDownloadReceipts.ToArrayAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private sealed class FixedOrderClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FailOrderSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("synthetic_save_failure");
    }
    private sealed class LostOrderCommit : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            throw new TimeoutException("synthetic_lost_acknowledgement");
    }
}
