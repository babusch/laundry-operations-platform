using System.Globalization;
using System.Text.Json.Serialization;

namespace Laundry.Api.Orders;

// Public representation deliberately excludes persistence/audit identity fields.
public sealed record OrderDetails(Guid Id, Guid PlantId, string OrderNumber, Guid CustomerId,
    DateOnly PlannedReceivingDate, DateOnly PlannedDeliveryDate, string? CustomerReference,
    string? Notes, string Status, long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? NotesVisibleToOperators = null)
{
    public static OrderDetails From(ServiceOrder order) => new(order.Id, order.PlantId, order.OrderNumber,
        order.CustomerId, order.PlannedReceivingDate, order.PlannedDeliveryDate, order.CustomerReference,
        order.Notes, order.Status, order.Version, order.CreatedAtUtc, order.UpdatedAtUtc, order.NotesVisibleToOperators);

    public string EntityTag() => $"\"{Id:D}:{Version.ToString(CultureInfo.InvariantCulture)}\"";
}
