using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Laundry.Edge.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Edge.Orders;

public sealed record LocalOrderCustomer(Guid Id, string Code, string DisplayName);
public sealed record LocalOrderSummary(Guid Id, string OrderNumber, LocalOrderCustomer Customer,
    DateOnly PlannedReceivingDate, DateOnly PlannedDeliveryDate, string Status, long Version);
public sealed record LocalOrderPage(LocalOrderSummary[] Items, string? NextCursor, bool HasMore,
    DateTimeOffset? LastSuccessfulPageAtUtc);
public sealed class LocalOrderListException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class LocalOrderList(PlantDbContext database, IDataProtectionProvider protection)
{
    private readonly IDataProtector _cursors = protection.CreateProtector("Laundry.Edge.LocalOrderList.v1");
    private sealed record Bookmark(Guid TenantId, Guid PlantId, long Revision, Guid AfterId);

    public async Task<LocalOrderPage> ReadAsync(OrderDownloadScope scope, int limit, string? cursor, CancellationToken ct)
    {
        if (scope.TenantId == Guid.Empty || scope.PlantId == Guid.Empty || limit is < 1 or > 50)
            throw new LocalOrderListException("invalid_local_order_query");
        Bookmark? bookmark = null;
        if (cursor is not null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 2048) throw new CryptographicException();
                bookmark = JsonSerializer.Deserialize<Bookmark>(_cursors.Unprotect(cursor));
                if (bookmark is null || bookmark.TenantId != scope.TenantId || bookmark.PlantId != scope.PlantId ||
                    bookmark.Revision < 0 || bookmark.AfterId == Guid.Empty) throw new CryptographicException();
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException)
            { throw new LocalOrderListException("invalid_local_order_cursor"); }
        }
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var progress = await database.OrderDownloadProgress.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.PlantId == scope.PlantId, ct);
        if (bookmark is not null && bookmark.Revision != (progress?.Revision ?? 0))
            throw new LocalOrderListException("local_order_list_changed");
        var after = bookmark?.AfterId ?? Guid.Empty;
        // Keyset ordering uses PostgreSQL UUID ordering, not device time or a cloud download bookmark.
        var rows = await database.LocalOrderPlans.FromSqlInterpolated($"""
            SELECT * FROM plant.order_plans
            WHERE tenant_id={scope.TenantId} AND plant_id={scope.PlantId} AND order_id>{after}
            ORDER BY order_id LIMIT {limit + 1}
            """).AsNoTracking().ToArrayAsync(ct);
        var items = rows.Take(limit).Select(row => Summary(row, scope)).ToArray();
        var more = rows.Length > limit;
        var next = more ? _cursors.Protect(JsonSerializer.Serialize(new Bookmark(scope.TenantId, scope.PlantId,
            progress?.Revision ?? 0, items[^1].Id))) : null;
        await transaction.CommitAsync(ct);
        return new(items, next, more, progress?.LastSuccessfulCheckUtc);
    }

    private static LocalOrderSummary Summary(LocalOrderPlan row, OrderDownloadScope scope)
    {
        try
        {
            var batch = OrderDownloadBatchParser.Parse("{\"items\":[" + row.Payload + "],\"nextCursor\":\"local-read\",\"hasMore\":false}", scope);
            if (batch.Items.Length != 1) throw new OrderDownloadException("invalid_order_publication");
            var item = batch.Items.Single();
            if (item.Id != row.OrderId || item.Version != row.Version || item.Fingerprint != row.ContentFingerprint)
                throw new OrderDownloadException("invalid_order_publication");
            using var document = JsonDocument.Parse(item.Payload);
            var json = document.RootElement; var customer = json.GetProperty("customer");
            return new(item.Id, json.GetProperty("orderNumber").GetString()!,
                new(customer.GetProperty("id").GetGuid(), customer.GetProperty("code").GetString()!, customer.GetProperty("displayName").GetString()!),
                DateOnly.ParseExact(json.GetProperty("plannedReceivingDate").GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                DateOnly.ParseExact(json.GetProperty("plannedDeliveryDate").GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                "planned", item.Version);
        }
        catch (OrderDownloadException) { throw new LocalOrderListException("local_order_copy_invalid"); }
    }
}
