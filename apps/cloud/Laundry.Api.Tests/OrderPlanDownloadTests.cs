using System.Text.Json;
using System.Data.Common;
using Laundry.Api.Orders;
using Laundry.Api.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Laundry.Api.Tests;

public sealed partial class OrderPlanningDatabaseTests
{
    private readonly IDataProtector _downloadProtector = new EphemeralDataProtectionProvider().CreateProtector(DevelopmentOrderPlanDownload.CursorPurpose);
    private GatewayScope DownloadScope => new(_scope.TenantId, _scope.PlantId);
    private OrderPlanDownloadStore Downloads => new(_options, _downloadProtector);

    [Fact]
    public async Task Download_PagesRetryAndEmptyPollThenNewUpdate()
    {
        var firstId = Id(await _store.CreateAsync(_scope, Guid.NewGuid(), _plan));
        var secondId = Id(await _store.CreateAsync(_scope, Guid.NewGuid(), _plan));
        var first = await Downloads.ReadAsync(DownloadScope, null, 1);
        Assert.True(first.HasMore); Assert.Equal(firstId, first.Items.Single().GetProperty("orderId").GetGuid());
        var second = await Downloads.ReadAsync(DownloadScope, first.NextCursor, 1);
        Assert.False(second.HasMore); Assert.Equal(secondId, second.Items.Single().GetProperty("orderId").GetGuid());
        var retry = await Downloads.ReadAsync(DownloadScope, first.NextCursor, 1);
        Assert.Equal(second.Items.Single().GetRawText(), retry.Items.Single().GetRawText());
        Assert.Empty((await Downloads.ReadAsync(DownloadScope, second.NextCursor)).Items);
        await _store.UpdateAsync(_scope, Guid.NewGuid(), firstId, 1, _plan with { PlannedDeliveryDate = _plan.PlannedDeliveryDate.AddDays(1) });
        var updated = await Downloads.ReadAsync(DownloadScope, second.NextCursor);
        Assert.Equal(firstId, updated.Items.Single().GetProperty("orderId").GetGuid());
        Assert.Equal(2, updated.Items.Single().GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Download_CurrentCopiesSkipSupersededNotesAndChangesBetweenPagesAreNotMissed()
    {
        var id = Id(await _store.CreateAsync(_scope, Guid.NewGuid(), _plan with { Notes = "Synthetic previously visible" }, notesVisibleToOperators: true));
        await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        var first = await Downloads.ReadAsync(DownloadScope, null, 1);
        await _store.UpdateAsync(_scope, Guid.NewGuid(), id, 1, _plan with { Notes = "Synthetic previously visible" }, notesVisibleToOperators: false);
        var next = await Downloads.ReadAsync(DownloadScope, first.NextCursor);
        Assert.Equal(2, next.Items.Count);
        var hidden = next.Items.Single(x => x.GetProperty("orderId").GetGuid() == id);
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("notes").ValueKind);
        var fresh = await Downloads.ReadAsync(DownloadScope, null);
        Assert.DoesNotContain("Synthetic previously visible", JsonSerializer.Serialize(fresh));
        Assert.All(fresh.Items, x => Assert.False(x.TryGetProperty("customerReference", out _)));
    }

    [Fact]
    public async Task Download_EmptyGenesisCanResumeAndOtherScopesStayEmpty()
    {
        var empty = await Downloads.ReadAsync(DownloadScope, null);
        Assert.Empty(empty.Items); Assert.False(empty.HasMore);
        await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        Assert.Single((await Downloads.ReadAsync(DownloadScope, empty.NextCursor)).Items);
        Assert.Empty((await Downloads.ReadAsync(new(_scope.TenantId, Guid.NewGuid()), null)).Items);
        Assert.Empty((await Downloads.ReadAsync(new(Guid.NewGuid(), _scope.PlantId), null)).Items);
    }

    [Fact]
    public async Task Download_RejectsTamperedCrossScopeAheadAndWrongEpochCursors()
    {
        await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        var page = await Downloads.ReadAsync(DownloadScope, null);
        var cursor = OrderDownloadCursor.Decode(_downloadProtector, page.NextCursor, DownloadScope);
        foreach (var invalid in new[] { "invalid", "", new string('x', 2049),
            (cursor with { Position = cursor.Position + 1 }).Encode(_downloadProtector),
            (cursor with { Epoch = Guid.NewGuid() }).Encode(_downloadProtector),
            (cursor with { TenantId = Guid.NewGuid() }).Encode(_downloadProtector),
            (cursor with { PlantId = Guid.NewGuid() }).Encode(_downloadProtector) })
            await Assert.ThrowsAsync<InvalidOrderDownloadCursorException>(() => Downloads.ReadAsync(DownloadScope, invalid));
    }

    [Fact]
    public async Task Download_RollbackDoesNotConsumePositionAndUnchangedContentDoesNotMoveIt()
    {
        var id = Id(await SeedUnpublishedOrderAsync());
        var failing = new DbContextOptionsBuilder<OrderDbContext>(_options).AddInterceptors(new RejectPublicationSave()).Options;
        await Assert.ThrowsAsync<InvalidOperationException>(() => StagePublication(id, options: failing));
        await using (var check = new OrderDbContext(_options)) Assert.Empty(await check.PublicationFeedStates.ToArrayAsync());
        await StagePublication(id);
        await StagePublication(id, edit: o => o.CustomerReference = "Synthetic management only");
        await using var db = new OrderDbContext(_options);
        Assert.Equal(1, (await db.PublicationFeedStates.SingleAsync()).LastPosition);
        Assert.Equal(1, (await db.PublicationHeads.SingleAsync()).ChangePosition);
    }

    [Fact]
    public async Task Download_UncommittedEarlierWriterCannotBeSkipped()
    {
        var id = Id(await SeedUnpublishedOrderAsync());
        await using var writer = new OrderDbContext(_options);
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await OrderPublicationStorage.LockPlantAsync(writer, _scope.TenantId, _scope.PlantId);
        var order = await writer.Orders.SingleAsync();
        // This legacy fixture has no numbering counter; avoid colliding with the later
        // ordinary create's first allocated number.
        order.OrderNumber = "SYNTHETIC-LEGACY";
        await OrderPublicationStorage.StageAsync(writer, _scope.TenantId, _scope.PlantId, order, PublicationCustomer(), TimeProvider.System);
        await writer.SaveChangesAsync();
        var beforeCommit = await Downloads.ReadAsync(DownloadScope, null);
        Assert.Empty(beforeCommit.Items);
        var laterWriter = _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        await transaction.CommitAsync(); await laterWriter;
        var afterCommit = await Downloads.ReadAsync(DownloadScope, beforeCommit.NextCursor);
        Assert.Equal(2, afterCommit.Items.Count);
        Assert.Equal(id, afterCommit.Items[0].GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task Download_RestartRetainsCursorAndPublicationPosition()
    {
        await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        var page = await Downloads.ReadAsync(DownloadScope, null);
        await _postgres.StopAsync(); await _postgres.StartAsync();
        var restarted = new OrderPlanDownloadStore(Options(_postgres.GetConnectionString()), _downloadProtector);
        Assert.Empty((await restarted.ReadAsync(DownloadScope, page.NextCursor)).Items);
        Assert.Single((await restarted.ReadAsync(DownloadScope, null)).Items);
    }

    [Fact]
    public async Task Download_MigrationPreservesExistingPayloadsAndOutbox()
    {
        var id = Id(await _store.CreateAsync(_scope, Guid.NewGuid(), _plan));
        await _store.CreateAsync(_scope, Guid.NewGuid(), _plan);
        await using var db = new OrderDbContext(_options);
        var before = await db.PublicationHeads.AsNoTracking().SingleAsync(x => x.OrderId == id);
        var evidence = JsonSerializer.Serialize(await db.PublicationOutbox.AsNoTracking().ToArrayAsync());
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261016200957_InitialPublicationAttribution");
        await migrator.MigrateAsync();
        var after = await db.PublicationHeads.AsNoTracking().SingleAsync(x => x.OrderId == id);
        Assert.Equal(before.Payload, after.Payload); Assert.Equal(before.Version, after.Version); Assert.Equal(before.ContentFingerprint, after.ContentFingerprint);
        Assert.Equal(evidence, JsonSerializer.Serialize(await db.PublicationOutbox.AsNoTracking().ToArrayAsync()));
        var downloaded = (await Downloads.ReadAsync(DownloadScope, null)).Items;
        Assert.Equal(2, downloaded.Count);
        Assert.Contains(downloaded, x => x.GetProperty("orderId").GetGuid() == id);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Download_PageUsesOneSnapshotWhenWriterCommitsBetweenStateAndHeads()
    {
        var id = Id(await _store.CreateAsync(_scope, Guid.NewGuid(), _plan with { Notes = "Synthetic in-flight note" }, notesVisibleToOperators: true));
        var options = new DbContextOptionsBuilder<OrderDbContext>(_options).AddInterceptors(new BeforeHeadQuery(async () =>
            await _store.UpdateAsync(_scope, Guid.NewGuid(), id, 1, _plan with { Notes = "Synthetic in-flight note" }, notesVisibleToOperators: false))).Options;
        var inFlight = await new OrderPlanDownloadStore(options, _downloadProtector).ReadAsync(DownloadScope, null);
        Assert.Equal(1, inFlight.Items.Single().GetProperty("version").GetInt64());
        var next = await Downloads.ReadAsync(DownloadScope, inFlight.NextCursor);
        Assert.Equal(2, next.Items.Single().GetProperty("version").GetInt64());
        Assert.Equal(JsonValueKind.Null, next.Items.Single().GetProperty("notes").ValueKind);
    }

    [Fact]
    public async Task Download_LostProtectionKeysFailExplicitlyInsteadOfResetting()
    {
        var page = await Downloads.ReadAsync(DownloadScope, null);
        var otherKeys = new EphemeralDataProtectionProvider().CreateProtector(DevelopmentOrderPlanDownload.CursorPurpose);
        await Assert.ThrowsAsync<InvalidOrderDownloadCursorException>(() =>
            new OrderPlanDownloadStore(_options, otherKeys).ReadAsync(DownloadScope, page.NextCursor));
    }

    private sealed class BeforeHeadQuery(Func<Task> action) : DbCommandInterceptor
    {
        private int _ran;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM orders.publication_heads", StringComparison.Ordinal) && Interlocked.Exchange(ref _ran, 1) == 0)
                await action();
            return result;
        }
    }
}
