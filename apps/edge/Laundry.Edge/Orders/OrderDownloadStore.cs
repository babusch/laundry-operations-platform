using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Laundry.Edge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Edge.Orders;

// Storage seam only: the future downloader must authenticate the HTTP response and supply
// this scope from trusted gateway configuration, never from response/query identities.
public sealed class OrderDownloadStore
{
    private readonly DbContextOptions<PlantDbContext> _options;
    private readonly OrderDownloadScope _scope;
    private readonly TimeProvider _clock;

    public OrderDownloadStore(DbContextOptions<PlantDbContext> options, OrderDownloadScope scope, TimeProvider clock)
    {
        if (scope.TenantId == Guid.Empty || scope.PlantId == Guid.Empty) throw new ArgumentException("Configure a nonempty order download scope.");
        _options = options; _scope = scope; _clock = clock;
    }

    public async Task<LocalOrderProgress> ReadProgressAsync(CancellationToken ct = default)
    {
        await using var db = new PlantDbContext(_options);
        var row = await db.OrderDownloadProgress.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == _scope.TenantId && x.PlantId == _scope.PlantId, ct);
        return row is null ? new(0, null, null) : new(row.Revision, row.Cursor, row.LastSuccessfulCheckUtc);
    }

    public async Task<OrderBatchResult> ApplyAsync(Guid batchId, long expectedProgressRevision, string? requestedCursor,
        string responseJson, CancellationToken ct = default)
    {
        if (batchId == Guid.Empty || expectedProgressRevision < 0 ||
            (requestedCursor is not null && !OrderDownloadBatchParser.ValidCursor(requestedCursor)))
            throw new OrderDownloadException("invalid_order_batch_command");
        var batch = OrderDownloadBatchParser.Parse(responseJson, _scope);
        var fingerprint = OrderDownloadBatchParser.Hash(JsonSerializer.Serialize(new
        {
            expectedProgressRevision,
            requestedCursor,
            batch.NextCursor,
            batch.HasMore,
            Items = batch.Items.Select(x => x.Payload).ToArray()
        }));
        await using var db = new PlantDbContext(_options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Separate from scan/outbox locks; always batch lock before plant download lock.
        await LockAsync(db, $"order-download-batch:{batchId:D}", ct);
        await LockAsync(db, $"order-download-scope:{_scope.TenantId:D}:{_scope.PlantId:D}", ct);
        var accepted = await db.OrderDownloadReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.BatchId == batchId, ct);
        if (accepted is not null)
        {
            if (accepted.TenantId != _scope.TenantId || accepted.PlantId != _scope.PlantId || accepted.RequestFingerprint != fingerprint)
                throw new OrderDownloadException("order_batch_id_conflict");
            return accepted.Result(); // Historical receipt; never move current progress back.
        }
        var progress = await db.OrderDownloadProgress.SingleOrDefaultAsync(x => x.TenantId == _scope.TenantId && x.PlantId == _scope.PlantId, ct);
        if ((progress?.Revision ?? 0) != expectedProgressRevision || !string.Equals(progress?.Cursor, requestedCursor, StringComparison.Ordinal))
            throw new OrderDownloadException("stale_order_download_progress");
        if (expectedProgressRevision == long.MaxValue) throw new OrderDownloadException("order_download_progress_exhausted");
        var now = _clock.GetUtcNow().ToUniversalTime();
        var receipt = new OrderDownloadBatchReceipt
        {
            BatchId = batchId,
            TenantId = _scope.TenantId,
            PlantId = _scope.PlantId,
            RequestFingerprint = fingerprint,
            ProgressRevision = expectedProgressRevision + 1,
            RecordedAtUtc = now
        };
        foreach (var item in batch.Items)
        {
            var local = await db.LocalOrderPlans.SingleOrDefaultAsync(x => x.TenantId == _scope.TenantId && x.PlantId == _scope.PlantId && x.OrderId == item.Id, ct);
            if (local is not null && local.Version == item.Version)
            {
                if (local.ContentFingerprint != item.Fingerprint || local.Payload != item.Payload)
                    throw new OrderDownloadException("order_publication_revision_conflict");
                receipt.Duplicates++; continue;
            }
            if (local is not null && local.Version > item.Version) { receipt.Older++; continue; }
            if (local is null)
            {
                local = new() { TenantId = _scope.TenantId, PlantId = _scope.PlantId, OrderId = item.Id };
                db.LocalOrderPlans.Add(local);
            }
            local.Version = item.Version; local.Payload = item.Payload; local.ContentFingerprint = item.Fingerprint; local.StoredAtUtc = now;
            receipt.Applied++;
        }
        if (progress is null)
        {
            progress = new() { TenantId = _scope.TenantId, PlantId = _scope.PlantId };
            db.OrderDownloadProgress.Add(progress);
        }
        progress.Revision = receipt.ProgressRevision; progress.Cursor = batch.NextCursor; progress.LastSuccessfulCheckUtc = now;
        db.OrderDownloadReceipts.Add(receipt);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return receipt.Result();
    }

    private static Task LockAsync(PlantDbContext db, string name, CancellationToken ct)
    {
        var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }
}
