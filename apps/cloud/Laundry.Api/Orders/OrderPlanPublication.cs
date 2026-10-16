using System.Text.Json.Serialization;

namespace Laundry.Api.Orders;

// Value supplied by the owning customer module, not a shared persistence entity.
public sealed record OrderPublicationCustomer(Guid TenantId, Guid Id, string Code, string DisplayName);
public sealed record PublishedOrderCustomer(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("displayName")] string DisplayName);

// A deliberately allowlisted working copy, not the management OrderDetails DTO.
public sealed record OrderPlanPublication
{
    private OrderPlanPublication() { }

    [JsonPropertyName("schemaVersion")] public int SchemaVersion => 1;
    [JsonPropertyName("tenantId")] public Guid TenantId { get; private init; }
    [JsonPropertyName("plantId")] public Guid PlantId { get; private init; }
    [JsonPropertyName("orderId")] public Guid OrderId { get; private init; }
    [JsonPropertyName("orderNumber")] public string OrderNumber { get; private init; } = "";
    [JsonPropertyName("customer")] public PublishedOrderCustomer Customer { get; private init; } = null!;
    [JsonPropertyName("plannedReceivingDate")] public DateOnly PlannedReceivingDate { get; private init; }
    [JsonPropertyName("plannedDeliveryDate")] public DateOnly PlannedDeliveryDate { get; private init; }
    [JsonPropertyName("status")] public string Status => "planned";
    [JsonPropertyName("notesVisibleToOperators")] public bool NotesVisibleToOperators { get; private init; }
    [JsonPropertyName("notes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Notes { get; private init; }
    [JsonPropertyName("version")] public long Version { get; private init; }

    // The caller must authorize/load a coherent snapshot and allocate this revision durably.
    // This pure mapper neither allocates revisions nor establishes gateway/customer eligibility.
    public static OrderPlanPublication From(Guid tenantId, Guid plantId, ServiceOrder order,
        OrderPublicationCustomer customer, long publicationVersion)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(customer);
        if (tenantId == Guid.Empty || plantId == Guid.Empty || order.Id == Guid.Empty || customer.Id == Guid.Empty ||
            order.TenantId != tenantId || order.PlantId != plantId || customer.TenantId != tenantId || order.CustomerId != customer.Id)
            throw new InvalidOperationException("order_publication_scope_mismatch");
        if (publicationVersion is < 1 or > 9007199254740991)
            throw new ArgumentOutOfRangeException(nameof(publicationVersion));
        if (order.Status != "planned" || string.IsNullOrWhiteSpace(order.OrderNumber) ||
            order.PlannedReceivingDate == default || order.PlannedDeliveryDate < order.PlannedReceivingDate ||
            string.IsNullOrWhiteSpace(customer.Code) || customer.Code.Length > 50 ||
            string.IsNullOrWhiteSpace(customer.DisplayName) || customer.DisplayName.Length > 200)
            throw new InvalidOperationException("invalid_order_publication");

        // Do not copy hidden text into any output field, even temporarily.
        var notes = order.NotesVisibleToOperators && !string.IsNullOrWhiteSpace(order.Notes) ? order.Notes.Trim() : null;
        if (notes?.Length > 2000) throw new InvalidOperationException("invalid_order_publication");
        return new()
        {
            TenantId = tenantId,
            PlantId = plantId,
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            Customer = new(customer.Id, customer.Code, customer.DisplayName),
            PlannedReceivingDate = order.PlannedReceivingDate,
            PlannedDeliveryDate = order.PlannedDeliveryDate,
            NotesVisibleToOperators = order.NotesVisibleToOperators,
            Notes = notes,
            Version = publicationVersion
        };
    }
}
