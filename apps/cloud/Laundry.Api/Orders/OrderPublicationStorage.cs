using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Orders;

public sealed class OrderPublicationHead
{
    public Guid TenantId { get; set; }
    public Guid PlantId { get; set; }
    public Guid OrderId { get; set; }
    public long Version { get; set; }
    public string ContentFingerprint { get; set; } = "";
    public string Payload { get; set; } = "";
}

// Immutable ready-to-send evidence. Delivery bookkeeping belongs to a later slice.
public sealed class OrderPublicationOutboxEntry
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PlantId { get; set; }
    public Guid OrderId { get; set; }
    public long Version { get; set; }
    public string Payload { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string? InitialPublicationActor { get; set; }
}

public sealed record OrderPublicationStage(long Version, bool Changed);

// Storage primitive only: no HTTP route, service registration, authorization or automatic backfill.
public static class OrderPublicationStorage
{
    // Call before loading/mutating orders, and resolve customer labels in the SAME transaction.
    // Matches the order writer's existing lock so future integration has one lock order.
    public static Task LockPlantAsync(OrderDbContext db, Guid tenantId, Guid plantId, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("publication_requires_transaction");
        if (tenantId == Guid.Empty || plantId == Guid.Empty) throw new InvalidOperationException("order_publication_scope_mismatch");
        var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"order-planning:{tenantId:D}:{plantId:D}")));
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }

    // The caller saves/commits order + head + outbox together. A fresh context per command is required.
    // Initial publication uses onlyIfMissing: existing copies are never revised by that operation.
    public static async Task<OrderPublicationStage> StageAsync(OrderDbContext db, Guid tenantId, Guid plantId,
        ServiceOrder order, OrderPublicationCustomer customer, TimeProvider clock, bool onlyIfMissing = false,
        CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("publication_requires_transaction");
        if (db.Entry(order).State == EntityState.Detached) throw new InvalidOperationException("publication_requires_tracked_order");
        // Validate scope and filter before constructing any stored representation.
        var comparable = JsonSerializer.Serialize(OrderPlanPublication.From(tenantId, plantId, order, customer, 1));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(comparable)));
        await LockPlantAsync(db, tenantId, plantId, ct);
        if (db.Entry(order).State != EntityState.Added)
        {
            var persisted = await db.Orders.FromSqlInterpolated($"SELECT * FROM orders.service_orders WHERE tenant_id = {tenantId} AND plant_id = {plantId} AND id = {order.Id} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (persisted is null || persisted.Version != db.Entry(order).OriginalValues.GetValue<long>(nameof(ServiceOrder.Version)))
                throw new InvalidOperationException("publication_stale_order");
        }
        // Use database row locking, not a previously tracked head, for revision allocation.
        if (db.ChangeTracker.Entries<OrderPublicationHead>().Any(x => x.Entity.TenantId == tenantId &&
            x.Entity.PlantId == plantId && x.Entity.OrderId == order.Id))
            throw new InvalidOperationException("publication_already_staged_in_context");
        var head = await db.PublicationHeads.FromSqlInterpolated($"SELECT * FROM orders.publication_heads WHERE tenant_id = {tenantId} AND plant_id = {plantId} AND order_id = {order.Id} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (head is not null && (onlyIfMissing || head.ContentFingerprint == fingerprint)) return new(head.Version, false);
        var version = head is null ? 1 : checked(head.Version + 1);
        var payload = JsonSerializer.Serialize(OrderPlanPublication.From(tenantId, plantId, order, customer, version));
        if (head is null)
        {
            head = new() { TenantId = tenantId, PlantId = plantId, OrderId = order.Id };
            db.PublicationHeads.Add(head);
        }
        head.Version = version; head.Payload = payload; head.ContentFingerprint = fingerprint;
        db.PublicationOutbox.Add(new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PlantId = plantId,
            OrderId = order.Id,
            Version = version,
            Payload = payload,
            RecordedAtUtc = clock.GetUtcNow()
        });
        return new(version, true);
    }
}
