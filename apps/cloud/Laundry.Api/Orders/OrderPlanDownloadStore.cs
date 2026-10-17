using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Laundry.Api.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Laundry.Api.Orders;

public sealed record OrderPlanDownloadPage(IReadOnlyList<JsonElement> Items, string NextCursor, bool HasMore);
public sealed class InvalidOrderDownloadCursorException : Exception;

// Cursor contents are protected and scoped; they are not authorization or delivery receipts.
public sealed record OrderDownloadCursor(Guid TenantId, Guid PlantId, Guid Epoch, long Position)
{
    public string Encode(IDataProtector protector) => protector.Protect(JsonSerializer.Serialize(this));

    public static OrderDownloadCursor Decode(IDataProtector protector, string value, GatewayScope scope)
    {
        if (value.Length is < 1 or > 2048) throw new InvalidOrderDownloadCursorException();
        try
        {
            var cursor = JsonSerializer.Deserialize<OrderDownloadCursor>(protector.Unprotect(value));
            if (cursor is null || cursor.TenantId != scope.TenantId || cursor.PlantId != scope.PlantId ||
                cursor.Position < 0 || (cursor.Epoch == Guid.Empty && cursor.Position != 0))
                throw new InvalidOrderDownloadCursorException();
            return cursor;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        { throw new InvalidOrderDownloadCursorException(); }
    }
}

public sealed class OrderPlanDownloadStore(DbContextOptions<OrderDbContext> options, IDataProtector protector)
{
    internal static async Task<long> AllocatePositionAsync(OrderDbContext db, Guid tenant, Guid plant, CancellationToken ct)
    {
        // Called only after acquiring the shared transaction-scoped plant lock.
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO orders.publication_feed_states (tenant_id, plant_id, epoch, last_position) VALUES ({tenant}, {plant}, {Guid.NewGuid()}, 1) ON CONFLICT (tenant_id, plant_id) DO UPDATE SET last_position = orders.publication_feed_states.last_position + 1", ct);
        return await db.PublicationFeedStates.AsNoTracking().Where(x => x.TenantId == tenant && x.PlantId == plant)
            .Select(x => x.LastPosition).SingleAsync(ct);
    }

    public async Task<OrderPlanDownloadPage> ReadAsync(GatewayScope scope, string? cursor, int limit = 100, CancellationToken ct = default)
    {
        if (scope.TenantId == Guid.Empty || scope.PlantId == Guid.Empty || limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var decoded = cursor is null ? null : OrderDownloadCursor.Decode(protector, cursor, scope);
        await using var db = new OrderDbContext(options);
        // State, positions and payloads must come from one coherent snapshot. No read locks
        // held against writers; a change after this snapshot appears on the next request.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var state = await db.PublicationFeedStates.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.PlantId == scope.PlantId, ct);
        var epoch = state?.Epoch ?? Guid.Empty;
        var highWater = state?.LastPosition ?? 0;
        if (decoded is not null && (decoded.Position > highWater ||
            (decoded.Epoch != epoch && !(decoded.Epoch == Guid.Empty && decoded.Position == 0))))
            throw new InvalidOrderDownloadCursorException();
        var position = decoded?.Position ?? 0;
        var rows = await db.PublicationHeads.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.PlantId == scope.PlantId && x.ChangePosition > position && x.ChangePosition <= highWater)
            .OrderBy(x => x.ChangePosition).Take(limit + 1).ToArrayAsync(ct);
        var page = rows.Take(limit).ToArray();
        var more = rows.Length > limit;
        var nextPosition = more ? page[^1].ChangePosition : highWater;
        var items = page.Select(x => JsonSerializer.Deserialize<JsonElement>(x.Payload)).ToArray();
        return new(items, new OrderDownloadCursor(scope.TenantId, scope.PlantId, epoch, nextPosition).Encode(protector), more);
    }
}
