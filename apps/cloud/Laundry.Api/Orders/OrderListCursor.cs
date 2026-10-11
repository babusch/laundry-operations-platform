using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Laundry.Api.Orders;

internal sealed record OrderListCursor(Guid TenantId, Guid PlantId, int Limit, DateOnly ReceivingDate, Guid OrderId)
{
    public static bool TryRead(IDataProtector protector, string value, OrderPlanningScope scope, int limit, out OrderListCursor? cursor)
    {
        cursor = null;
        if (value.Length is 0 or > 2048) return false;
        try
        {
            cursor = JsonSerializer.Deserialize<OrderListCursor>(protector.Unprotect(value));
            return cursor is not null && cursor.TenantId == scope.TenantId && cursor.PlantId == scope.PlantId &&
                cursor.Limit == limit && cursor.ReceivingDate != default && cursor.OrderId != Guid.Empty;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            return false;
        }
    }

    public string Encode(IDataProtector protector) => protector.Protect(JsonSerializer.Serialize(this));
}

public sealed record OrderListPage(OrderDetails[] Items, string? NextCursor);
