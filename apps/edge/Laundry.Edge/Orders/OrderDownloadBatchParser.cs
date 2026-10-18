using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace Laundry.Edge.Orders;

internal sealed record ValidatedOrder(Guid Id, long Version, string Payload, string Fingerprint);
internal sealed record ValidatedOrderBatch(ValidatedOrder[] Items, string NextCursor, bool HasMore);

internal static class OrderDownloadBatchParser
{
    private static readonly JsonSchema Schema = LoadSchema();
    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(OrderDownloadBatchParser).Assembly.GetManifestResourceStream("Contracts.OrderPlanV1.json")
            ?? throw new InvalidOperationException("The published order contract is missing.");
        using var json = JsonDocument.Parse(stream);
        return JsonSchema.Build(json.RootElement.Clone(), new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() });
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool ValidCursor(string? cursor) => !string.IsNullOrWhiteSpace(cursor) && cursor.Length <= 2048;

    internal static ValidatedOrderBatch Parse(string json, OrderDownloadScope scope)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024)
            throw new OrderDownloadException("invalid_order_batch");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (HasDuplicateProperties(root) || root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
                !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100 ||
                !root.TryGetProperty("nextCursor", out var cursor) || cursor.ValueKind != JsonValueKind.String || !ValidCursor(cursor.GetString()) ||
                !root.TryGetProperty("hasMore", out var more) || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                (more.GetBoolean() && items.GetArrayLength() == 0))
                throw new OrderDownloadException("invalid_order_batch");
            var result = new List<ValidatedOrder>();
            var seen = new HashSet<Guid>();
            foreach (var item in items.EnumerateArray())
            {
                if (!Schema.Evaluate(item, new EvaluationOptions { RequireFormatValidation = true }).IsValid)
                    throw new OrderDownloadException("invalid_order_publication");
                var tenant = item.GetProperty("tenantId").GetGuid(); var plant = item.GetProperty("plantId").GetGuid();
                if (tenant != scope.TenantId || plant != scope.PlantId) throw new OrderDownloadException("order_download_scope_mismatch");
                var id = item.GetProperty("orderId").GetGuid();
                if (!seen.Add(id)) throw new OrderDownloadException("duplicate_order_in_batch");
                if (!DateOnly.TryParseExact(item.GetProperty("plannedReceivingDate").GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var receiving) || receiving == default ||
                    !DateOnly.TryParseExact(item.GetProperty("plannedDeliveryDate").GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var delivery) || delivery < receiving)
                    throw new OrderDownloadException("invalid_order_publication");
                var customer = item.GetProperty("customer");
                var version = decimal.ToInt64(item.GetProperty("version").GetDecimal());
                // Fixed typed construction normalizes JSON layout, UUID casing, escaped text
                // and mathematically integral number representations, not business text.
                var payload = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    tenantId = tenant,
                    plantId = plant,
                    orderId = id,
                    orderNumber = item.GetProperty("orderNumber").GetString(),
                    customer = new { id = customer.GetProperty("id").GetGuid(), code = customer.GetProperty("code").GetString(), displayName = customer.GetProperty("displayName").GetString() },
                    plannedReceivingDate = receiving,
                    plannedDeliveryDate = delivery,
                    status = "planned",
                    notesVisibleToOperators = item.GetProperty("notesVisibleToOperators").GetBoolean(),
                    notes = item.GetProperty("notes").ValueKind == JsonValueKind.Null ? null : item.GetProperty("notes").GetString(),
                    version
                });
                result.Add(new(id, version, payload, Hash(payload)));
            }
            return new(result.ToArray(), cursor.GetString()!, more.GetBoolean());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
        { throw new OrderDownloadException("invalid_order_batch"); }
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            return value.EnumerateObject().Any(x => !names.Add(x.Name) || HasDuplicateProperties(x.Value));
        }
        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(HasDuplicateProperties);
    }
}
