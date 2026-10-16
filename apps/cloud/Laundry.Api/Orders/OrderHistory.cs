using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Laundry.Api.Orders;

// Public history contains planning evidence, not persistence or authentication identities.
public sealed record OrderPlanChange(string Field, string? Before, string? After);
public sealed record OrderHistoryPlan(Guid CustomerId, DateOnly PlannedReceivingDate, DateOnly PlannedDeliveryDate,
    string? CustomerReference, string? Notes, bool? NotesVisibleToOperators = null);
public sealed record OrderHistoryEntry(long Version, string Action, DateTimeOffset RecordedAtUtc,
    string? ActorDisplayName, OrderHistoryPlan? InitialPlan, OrderPlanChange[] Changes)
{
    public static OrderHistoryEntry From(OrderAuditEntry entry)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var after = JsonSerializer.Deserialize<OrderHistoryPlan>(entry.AfterPlan, options)
            ?? throw new JsonException("Invalid stored order history.");
        if (entry.BeforePlan is null)
            return new(entry.OrderVersion, entry.Action, entry.RecordedAtUtc, entry.ActorDisplayName, after, []);
        using var beforeJson = JsonDocument.Parse(entry.BeforePlan);
        using var afterJson = JsonDocument.Parse(entry.AfterPlan);
        var changes = new List<OrderPlanChange>();
        foreach (var field in new[] { "customerId", "plannedReceivingDate", "plannedDeliveryDate", "customerReference", "notes", "notesVisibleToOperators" })
        {
            var previous = Value(beforeJson.RootElement, field);
            var next = Value(afterJson.RootElement, field);
            if (previous != next) changes.Add(new(field, previous, next));
        }
        return new(entry.OrderVersion, entry.Action, entry.RecordedAtUtc, entry.ActorDisplayName, null, changes.ToArray());
    }

    private static string? Value(JsonElement plan, string field)
    {
        if (field == "notesVisibleToOperators" && plan.ValueKind == JsonValueKind.Object)
        {
            // Missing legacy evidence is unknown, not a guessed historical hidden choice.
            if (!plan.TryGetProperty(field, out var visibility) || visibility.ValueKind == JsonValueKind.Null) return null;
            return visibility.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => throw new JsonException("Invalid stored order history.")
            };
        }
        if (plan.ValueKind != JsonValueKind.Object || !plan.TryGetProperty(field, out var value) ||
            value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new JsonException("Invalid stored order history.");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }
}

public sealed record OrderHistoryPage(OrderHistoryEntry[] Items, string? NextCursor);

internal sealed record OrderHistoryCursor(Guid TenantId, Guid PlantId, Guid OrderId, int Limit, long AfterVersion)
{
    public string Encode(IDataProtector protector) => protector.Protect(JsonSerializer.Serialize(this));
    public static bool TryRead(IDataProtector protector, string value, OrderPlanningScope scope, Guid orderId, int limit, out OrderHistoryCursor? cursor)
    {
        cursor = null;
        if (value.Length is 0 or > 2048) return false;
        try
        {
            cursor = JsonSerializer.Deserialize<OrderHistoryCursor>(protector.Unprotect(value));
            return cursor is not null && cursor.TenantId == scope.TenantId && cursor.PlantId == scope.PlantId &&
                cursor.OrderId == orderId && cursor.Limit == limit && cursor.AfterVersion > 0;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException) { return false; }
    }
}
